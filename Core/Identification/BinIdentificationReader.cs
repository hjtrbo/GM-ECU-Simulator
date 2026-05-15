using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Core.Identification.Segments;

namespace Core.Identification;

// Parses a GM ECU flash image (T43 TCM, E38 PCM, E67 PCM) by tracing the
// $1A ReadDataByIdentifier handler in PowerPC machine code. The structure
// is the same across all three families - GM standardised the diagnostic
// core - so a single walker discovers every supported DID and its source
// flash address without family-specific hardcoding.
//
// Walk:
//   1. Locate the service dispatcher: a cmpwi/beq chain comparing the SID
//      byte against the supported services. Found by dense clustering of
//      cmpwi-imm against known GMW3110 SIDs ($1A, $20, $27, $28, $34, $36,
//      $3E, $A2, $A5).
//   2. Take the branch target for $1A. That's a trampoline that loads
//      `lbz r3, 3(rX)` (the DID byte from request offset 3) and bl's the
//      real DID dispatcher.
//   3. Walk the DID dispatcher's cmpwi/beq chain on r3, producing a map
//      DID -> handler offset.
//   4. For each handler, look for the indirect-call pattern:
//          lis  r12, X
//          lwz  r12, Y(r12)         ; load function pointer from flash table
//          mtctr r12 / mtlr r12
//          bctr  / blr
//      Two fetcher shapes turn up:
//        a. Bare: `lis r3, X ; addi r3, r3, Y ; blr` - returns a flash
//           address directly. $C1 across all three families.
//        b. With prologue: `stwu/mflr/stw ...` saves regs, then a helper-
//           call (lazy NV-cache init), then `lis r3, X ; addi r3, r3, Y`
//           produces the return address, then an epilogue + blr. The
//           address it returns is in chip RAM, not flash - the actual
//           data is populated at runtime from NVM. $CB and $CC across
//           all three families use this shape.
//      Both end with r3 holding the returned address before blr, so the
//      walker just scans the fn body for the lis/addi pair targeting r3.
//
// What survives across families:
//   - The 9 SID set
//   - The 6-DID set ($B0, $C1, $C9, $CA, $CB, $CC)
//   - The trampoline + indirect-call pattern
//
// What varies per family:
//   - Absolute dispatcher offsets
//   - The flash address each DID handler points at ($C1 lives at 0x060005
//     on T43, 0x010005 on E38/E67 in observed samples)
//   - The RAM-cache address $CB/$CC fetchers return (T43 0x302528/0x302578;
//     E38 0x3F8500/0x3F8508; E67 varies per cal, e.g. 0x3F7940/0x3F7948
//     on the 2016 HSV LSA tune). Tagged DidSourceKind.RuntimeComputed.
//
// Also extracts bonus metadata that isn't on the $1A wire path - VIN, Bosch
// trace code, HW version stamp - by regex over the supplier-data region.
// Real ECUs don't return these via $1A on these families, but they're
// present in flash for human inspection and are useful for populating the
// simulator's identity panel as informational defaults.
public static class BinIdentificationReader
{
    // ---------------------------- public API ----------------------------

    public enum DidSourceKind
    {
        FlashUInt32BE,    // handler reads 4 BE bytes from a fixed flash address
        InlineConstant,   // handler returns a hardcoded value (e.g. $B0 -> 0x18)
        RuntimeComputed,  // fetcher returns a RAM address - value is built at boot
        Unknown,
    }

    public sealed record DidExtraction(
        byte Did,
        DidSourceKind Kind,
        int? FlashAddress,
        byte[] WireBytes,
        string DecodedValue);

    public sealed record BinIdentification(
        string Family,                // "T43" / "E38" / "E67" / "Unknown"
        int ServiceDispatcherOffset,
        int Service1AHandlerOffset,
        int DidDispatcherOffset,
        IReadOnlyList<byte> SupportedSids,
        IReadOnlyList<DidExtraction> Dids,
        // bonus informational fields (not on $1A wire path, but present in flash)
        string? Vin,
        string? SupplierHardwareNumber,
        string? SupplierHardwareVersion,
        string? EndModelPartNumber,
        string? BaseModelPartNumber,
        // segment-reader extractions (Stage 2 fields)
        string? CalibrationPartNumber,   // PCM - the calibration broadcast PN as a decimal string
        string? BroadcastCode,           // BCC - last 4 chars of cal PN (T43 only; computed for E38/E67)
        string? ProgrammingDate,         // BCD date, e.g. "20220326"
        string? ProgrammingTool,         // tool ID stamp
        string? TraceCode,               // 16-char Bosch/Delphi physical trace stamp
        IReadOnlyList<string> Warnings)
    {
        public DidExtraction? FindDid(byte did) => Dids.FirstOrDefault(x => x.Did == did);
    }

    /// <summary>
    /// Parse a GM ECU flash image. Returns null if the bin is too small or
    /// no service dispatcher can be located.
    /// </summary>
    public static BinIdentification? Parse(ReadOnlySpan<byte> bin)
    {
        if (bin.Length < 0x10000) return null;

        var warnings = new List<string>();
        var bytes = bin.ToArray();

        // 1. Hunt the service dispatcher.
        if (!TryFindServiceDispatcher(bytes, out var dispatcherAnchor, out var dispatcherSids))
        {
            warnings.Add("Could not locate service dispatcher (no SID cluster found).");
            return null;
        }

        // 2. Walk the SID chain at the anchor.
        var sidMap = WalkChain(bytes, dispatcherAnchor, isDid: false);
        if (!sidMap.TryGetValue(0x1A, out var handler1A))
        {
            warnings.Add("Service dispatcher found but $1A is not handled.");
            return null;
        }

        // 3. Follow the $1A trampoline to the real DID dispatcher.
        if (!TryFollowTrampoline(bytes, handler1A, out var didDispatcher))
        {
            warnings.Add($"$1A handler at 0x{handler1A:X6} is not a trampoline; inline dispatch not yet supported.");
            return null;
        }

        // 4. Walk the DID chain.
        var didMap = WalkChain(bytes, didDispatcher, isDid: true);

        // 5. Trace each DID handler.
        var dids = new List<DidExtraction>();
        foreach (var (did, hOff) in didMap.OrderBy(kv => kv.Key))
        {
            // Skip pseudo-DIDs that the chain bounces against but doesn't really
            // support (e.g. $C9/$CA explicitly route to the negative-response path).
            var extracted = TraceDidHandler(bytes, did, hOff);
            if (extracted != null) dids.Add(extracted);
        }

        // 6. Family detection.
        var family = DetectFamily(bytes);

        // 7. Bonus metadata via flash patterns.
        ExtractFlashMetadata(bytes, family,
            out var vin, out var hwNum, out var hwVer, out var endPn, out var basePn);

        // 7b. Where we have a segment definition for this family, prefer
        // its structural extractions over the regex fallbacks. The segment
        // reader uses CheckWord markers and known field offsets so it
        // produces deterministic results (or null) rather than pattern-
        // matching plausible-looking byte runs.
        string? calPn = null, bcc = null, programDate = null, programTool = null, traceCode = null;
        var famDef = GmFamilyDefinitions.Lookup(family);
        if (famDef != null)
        {
            var segMatches = SegmentReader.Read(bytes, famDef);
            foreach (var match in segMatches)
            {
                foreach (var f in match.Fields)
                {
                    if (string.IsNullOrEmpty(f.DecodedValue)) continue;
                    switch (f.Name)
                    {
                        case "VIN":         vin = f.DecodedValue; break;
                        case "TraceCode":   traceCode = f.DecodedValue; break;
                        case "PCM":         calPn = f.DecodedValue; break;
                        case "BCC":         bcc = f.DecodedValue; break;
                        case "Programdate": programDate = FormatBcdDate(f.RawBytes) ?? f.DecodedValue; break;
                        case "Tool":        programTool = f.DecodedValue; break;
                    }
                }
            }
        }
        // Don't conflate $92 SupplierHardwareNumber with the EEPROM
        // TraceCode - they're semantically distinct even when they
        // sometimes share a value on Bosch-supplied bins. $92 is what the
        // ECU would return on the $1A wire path (frequently empty for
        // E38/E67); TraceCode is the physical EEPROM stamp.
        // E38/E67 don't carry a separate BCC field but it's defined as
        // "last 4 digits of the calibration PN" by GM convention. If the
        // segment reader gave us a cal PN but no BCC, derive one.
        if (bcc == null && !string.IsNullOrEmpty(calPn) && calPn!.Length >= 4)
            bcc = calPn[^4..];

        // If $1A doesn't expose $C1 directly, the flash-metadata fallback may
        // still find it - prefer the $1A-traced value when available.
        var c1Extracted = dids.FirstOrDefault(x => x.Did == 0xC1);
        if (c1Extracted != null && c1Extracted.Kind == DidSourceKind.FlashUInt32BE)
            endPn = c1Extracted.DecodedValue;

        return new BinIdentification(
            Family: family,
            ServiceDispatcherOffset: dispatcherAnchor,
            Service1AHandlerOffset: handler1A,
            DidDispatcherOffset: didDispatcher,
            SupportedSids: sidMap.Keys.OrderBy(b => b).ToArray(),
            Dids: dids,
            Vin: vin,
            SupplierHardwareNumber: hwNum,
            SupplierHardwareVersion: hwVer,
            EndModelPartNumber: endPn,
            BaseModelPartNumber: basePn,
            CalibrationPartNumber: calPn,
            BroadcastCode: bcc,
            ProgrammingDate: programDate,
            ProgrammingTool: programTool,
            TraceCode: traceCode,
            Warnings: warnings);
    }

    /// <summary>
    /// Format a 4-byte BCD programming-date stamp as <c>YYYYMMDD</c>. The
    /// raw bytes encode the four digit pairs directly (e.g. <c>20 22 03
    /// 26</c> -> "20220326"). Returns null if any nibble isn't a decimal
    /// digit, in which case the caller should fall back to a raw-hex
    /// rendering.
    /// </summary>
    private static string? FormatBcdDate(byte[] bytes)
    {
        if (bytes.Length != 4) return null;
        var sb = new StringBuilder(8);
        foreach (var b in bytes)
        {
            int hi = b >> 4;
            int lo = b & 0xF;
            if (hi > 9 || lo > 9) return null;
            sb.Append((char)('0' + hi));
            sb.Append((char)('0' + lo));
        }
        return sb.ToString();
    }

    // -------------------------- dispatcher hunt --------------------------

    // Classic GMW3110 service IDs we expect a real diagnostic dispatcher to
    // compare against. Five or more present in a 256-byte window is a near-
    // certain dispatcher signature.
    private static readonly byte[] KnownSids =
    {
        0x10, 0x11, 0x14, 0x1A, 0x20, 0x22, 0x23, 0x27, 0x28, 0x2C, 0x2D,
        0x31, 0x34, 0x35, 0x36, 0x37, 0x3B, 0x3D, 0x3E,
        0xA0, 0xA1, 0xA2, 0xA5, 0xA9, 0xAA, 0xAE,
    };

    private static bool TryFindServiceDispatcher(byte[] d, out int anchor, out byte[] sids)
    {
        // Map (windowStart -> set of distinct SIDs found there). Choose the
        // window with the most distinct SIDs.
        var windows = new Dictionary<int, HashSet<byte>>();
        foreach (var sid in KnownSids)
            foreach (var off in FindCmpwiImm(d, sid))
            {
                var w = off & ~0xFF;
                if (!windows.TryGetValue(w, out var set)) windows[w] = set = new HashSet<byte>();
                set.Add(sid);
            }

        var best = windows.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
        if (best.Value == null || best.Value.Count < 4)
        {
            anchor = 0; sids = Array.Empty<byte>(); return false;
        }

        // Anchor on the first $1A cmpwi inside the window (if present), else
        // any cmpwi inside it.
        anchor = -1;
        foreach (var off in FindCmpwiImm(d, 0x1A))
            if ((off & ~0xFF) == best.Key) { anchor = off; break; }
        if (anchor < 0)
            foreach (var sid in best.Value)
                foreach (var off in FindCmpwiImm(d, sid))
                    if ((off & ~0xFF) == best.Key) { anchor = off; break; }
        sids = best.Value.OrderBy(b => b).ToArray();
        return anchor >= 0;
    }

    /// <summary>
    /// Walk a cmpwi/beq chain starting at or just before <paramref name="anchor"/>.
    /// Returns a map: immediate value -> branch target.
    /// </summary>
    private static Dictionary<byte, int> WalkChain(byte[] d, int anchor, bool isDid)
    {
        // For the SID chain we first walk backwards to find the `lbz r?, 2(rX)`
        // that loads the SID byte (anchor is somewhere inside the chain, so
        // back up to the head before walking forward). The DID chain dispatcher
        // is called as a function so the anchor IS the head; no back-walk.
        int start = anchor;
        if (!isDid)
        {
            for (int back = 0; back < 0x100; back += 4)
            {
                int i = anchor - back;
                if (i < 0) break;
                uint w = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i, 4));
                if ((w >> 26) == 34 && (w & 0xFFFF) == 0x0002) { start = i; break; }  // lbz r?, 2(rX)
            }
        }

        var map = new Dictionary<byte, int>();
        int end = Math.Min(d.Length - 8, start + 0x200);
        int hitBranch = 0;
        for (int i = start; i < end; i += 4)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i, 4));
            uint op = w >> 26;
            if (op == 11)  // cmpwi
            {
                int imm = (short)(w & 0xFFFF);
                if (imm < 0 || imm > 0xFF) continue;
                // Next instruction should be a conditional branch (op 16) that
                // is the dispatch arm for this immediate.
                uint w2 = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i + 4, 4));
                uint op2 = w2 >> 26;
                if (op2 == 16)
                {
                    int bd = (short)(w2 & 0xFFFC);
                    int tgt = (i + 4 + bd) & 0x7FFFFFFF;
                    // Only record beq forms (BO=12, BI bit 2 -> eq). Other forms
                    // (bgt) split the chain and don't map a SID -> handler.
                    uint bo = (w2 >> 21) & 0x1F;
                    uint bi = (w2 >> 16) & 0x1F;
                    if (bo == 12 && (bi & 3) == 2)
                        map.TryAdd((byte)imm, tgt);
                }
            }
            else if (op == 18)
            {
                // Unconditional branch usually closes a chain half. Allow one
                // more half (the "upper" branch after a `bgt`), then stop.
                if (++hitBranch >= 2) break;
            }
        }
        return map;
    }

    private static bool TryFollowTrampoline(byte[] d, int handler1A, out int didDispatcher)
    {
        // The $1A handler does some flag writes, then:
        //   lbz r3, 3(rX)    ; rt=3, ra=any, simm=3
        //   bl  did_dispatcher
        didDispatcher = 0;
        int end = Math.Min(d.Length - 4, handler1A + 0x40);
        bool seenLbz = false;
        for (int i = handler1A; i < end; i += 4)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i, 4));
            uint op = w >> 26;
            if (op == 34)  // lbz
            {
                uint rt = (w >> 21) & 0x1F;
                int si = (short)(w & 0xFFFF);
                if (rt == 3 && si == 3) seenLbz = true;
            }
            else if (seenLbz && op == 18 && (w & 1) != 0)  // bl
            {
                int li = (int)(w & 0x03FFFFFC);
                if ((li & 0x02000000) != 0) li -= 0x04000000;
                didDispatcher = (i + li) & 0x7FFFFFFF;
                return didDispatcher > 0 && didDispatcher < d.Length;
            }
        }
        return false;
    }

    private static DidExtraction? TraceDidHandler(byte[] d, byte did, int handlerOff)
    {
        // The pattern we're looking for inside the handler body:
        //   lis  r12, X
        //   lwz  r12, Y(r12)      ; load fn ptr from flash table at (X<<16 | Y)
        //   mtctr/mtlr r12
        //   bctr/blr
        // Followed by the caller using the returned pointer to copy bytes.
        int end = Math.Min(d.Length - 16, handlerOff + 0x80);
        for (int i = handlerOff; i < end; i += 4)
        {
            uint w1 = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i, 4));
            uint w2 = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i + 4, 4));
            if ((w1 >> 26) != 15 || (w2 >> 26) != 32) continue;  // need lis ; lwz
            // lwz must reuse the lis destination as its base register, else
            // it's some unrelated load.
            uint lisRt = (w1 >> 21) & 0x1F;
            uint lwzRa = (w2 >> 16) & 0x1F;
            if (lisRt != lwzRa) continue;

            int hi = (int)(w1 & 0xFFFF);
            int lo = (short)(w2 & 0xFFFF);
            int tableAddr = (hi << 16) + lo;
            if (tableAddr < 0 || tableAddr + 4 > d.Length) continue;

            int fnPtr = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(tableAddr, 4));
            if (fnPtr < 0 || fnPtr + 12 > d.Length) continue;

            // Resolve the fetcher fn's return value. Two shapes observed:
            //   - Simple ($C1): `lis r3, X ; addi r3, r3, Y ; blr`.
            //   - With prologue ($CB/$CC): `stwu/mflr/stw...` then helper-call
            //     to lazy-init a RAM cache, then `lis r3, X ; addi r3, r3, Y`
            //     producing the cache address, then function epilogue + blr.
            // Both end with r3 holding the returned address before blr.
            if (!TryResolveFetcherReturn(d, fnPtr, out var dataAddr)) continue;

            // RAM-cached responses live above the flash image (chip RAM region).
            // We can identify them statically but can't read the value - the
            // RAM contents are populated at boot from NVM, not from a fixed
            // flash literal. Report the resolved RAM address in the note so
            // downstream tooling has something to display.
            if (dataAddr >= d.Length || dataAddr < 0)
            {
                return new DidExtraction(did, DidSourceKind.RuntimeComputed,
                    FlashAddress: null, WireBytes: Array.Empty<byte>(),
                    DecodedValue: $"runtime-computed RAM@0x{dataAddr:X6} (fetcher@0x{fnPtr:X6})");
            }

            // Direct flash read - the simulator's wire response is 4 bytes BE
            // starting at dataAddr. Decode as uint32 BE for the decimal display.
            var wire = d.AsSpan(dataAddr, 4).ToArray();
            uint value = BinaryPrimitives.ReadUInt32BigEndian(wire);
            return new DidExtraction(did, DidSourceKind.FlashUInt32BE,
                FlashAddress: dataAddr, WireBytes: wire,
                DecodedValue: value.ToString());
        }

        // No indirect-call pattern - handler is an inline constant builder.
        // Probe nearby `li rX, imm` bytes to surface a likely candidate, but
        // don't try to be too clever; report as inline and let the caller
        // decide whether to display anything.
        return new DidExtraction(did, DidSourceKind.InlineConstant,
            FlashAddress: null, WireBytes: Array.Empty<byte>(),
            DecodedValue: "(inline)");
    }

    /// <summary>
    /// Walk a DID fetcher fn body looking for the address it returns in r3.
    /// Matches both the bare `lis r3 ; addi r3 ; blr` shape (used by $C1 across
    /// all three families) and the longer "prologue + helper-call + lis r3 ;
    /// addi r3 + epilogue + blr" shape (used by $CB and $CC). The marker is a
    /// `lis r3, X` followed within a handful of instructions by `addi r3, r3, Y`
    /// before the function's terminating blr.
    /// </summary>
    private static bool TryResolveFetcherReturn(byte[] d, int fnPtr, out int dataAddr)
    {
        dataAddr = 0;
        int end = Math.Min(d.Length - 4, fnPtr + 0x80);
        for (int i = fnPtr; i < end; i += 4)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i, 4));
            if (w == 0x4E800020) return false;  // blr without finding the pair
            if ((w >> 26) != 15) continue;       // not lis
            if (((w >> 21) & 0x1F) != 3) continue; // not lis r3
            int hi = (int)(w & 0xFFFF);
            // Scan the next few instructions for `addi r3, r3, Y` while r3
            // hasn't been clobbered by another lis or by the blr.
            int scanEnd = Math.Min(end, i + 7 * 4);
            for (int j = i + 4; j < scanEnd; j += 4)
            {
                uint w2 = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(j, 4));
                if (w2 == 0x4E800020) return false; // blr before addi - r3 not finalised
                if ((w2 >> 26) == 14 && ((w2 >> 21) & 0x1F) == 3 && ((w2 >> 16) & 0x1F) == 3)
                {
                    int lo = (short)(w2 & 0xFFFF);
                    dataAddr = (hi << 16) + lo;
                    return true;
                }
                if ((w2 >> 26) == 15 && ((w2 >> 21) & 0x1F) == 3)
                {
                    // r3 overwritten by another lis - restart search from there.
                    break;
                }
            }
        }
        return false;
    }

    // ----------------------- PPC primitive helpers -----------------------

    // PPC instructions are 4-byte aligned and big-endian on this chip.
    // cmpwi / cmplwi rA, simm encodes as 0x2C / 0x28 in the high byte, with
    // the 16-bit immediate occupying the low two bytes. We scan the whole
    // image for these byte signatures.
    private static IEnumerable<int> FindCmpwiImm(byte[] d, byte imm)
    {
        for (int i = 0; i < d.Length - 4; i += 4)
        {
            if ((d[i] == 0x2C || d[i] == 0x28) && d[i + 2] == 0x00 && d[i + 3] == imm)
                yield return i;
        }
    }

    // ---------------------- family detection / metadata ----------------------

    private static string DetectFamily(byte[] d)
    {
        // T43 has a distinctive "BOSCH TC19.12" marker near 0x1FFA0 (end of
        // the Bosch project header). Strongest single signature we have.
        if (FindAscii(d, "BOSCH TC19.12") >= 0) return "T43";
        // E38 vs E67 distinguishable by VIN-descriptor block location: E38
        // and the older E67 keep it at 0xE0AC; the 2016+ E67 moved it to
        // 0xC0AC when the memory map shifted.
        if (LooksLikeE67(d)) return "E67";
        if (LooksLikeE38(d)) return "E38";
        return "Unknown";
    }

    private static bool LooksLikeE67(byte[] d)
    {
        // E67 (Bosch ME9): calibration PN at 0x1000E; check both common VIN
        // block offsets.
        if (HasAsciiVinDescriptor(d, 0xC0AC)) return true;
        // Some 2009-era E67 bins keep the VIN descriptor at 0xE0AC too -
        // disambiguate by looking for absence of the Delphi marker.
        if (HasAsciiVinDescriptor(d, 0xE0AC) && FindAscii(d, "DELPHI") < 0)
        {
            // E67 typically has Bosch markers too even though it's a
            // different platform than the T43 TC19.12.
            return FindAscii(d, "BOSCH") >= 0;
        }
        return false;
    }

    private static bool LooksLikeE38(byte[] d)
    {
        // E38 (Delphi): VIN block at 0xE0AC, no Bosch markers.
        return HasAsciiVinDescriptor(d, 0xE0AC) && FindAscii(d, "BOSCH") < 0;
    }

    private static bool HasAsciiVinDescriptor(byte[] d, int off)
    {
        // The descriptor block has the form `<8-char tail><17-char VIN>`. We
        // only need to confirm 25 printable ASCII bytes here; full VIN
        // validation happens in ExtractFlashMetadata.
        if (off < 0 || off + 25 > d.Length) return false;
        for (int i = 0; i < 25; i++)
        {
            byte b = d[off + i];
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    private static int FindAscii(byte[] d, string s)
    {
        var needle = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i + needle.Length <= d.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (d[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    // VIN charset excludes I, O, Q (per ISO 3779). Two flavours:
    //   - VinRx: plain 17-char VIN. Used as a last-resort fallback.
    //   - VinDescriptorRx: GM stores the VIN in flash as an 8-char "tail"
    //     (the last 8 chars of the VIN) immediately followed by the full
    //     17-char VIN. Requiring the suffix-match disambiguates between the
    //     real VIN and the leading 17 chars of `tail + VIN` (which would
    //     otherwise be captured as a junk VIN by the bare regex).
    private static readonly Regex VinRx = new(@"[A-HJ-NPR-Z0-9]{17}", RegexOptions.Compiled);
    private static readonly Regex VinDescriptorRx =
        new(@"([A-HJ-NPR-Z0-9]{8})([A-HJ-NPR-Z0-9]{17})", RegexOptions.Compiled);
    // Bosch project + HW number: e.g. DV5053Q103619515. 2 letters, 4 digits,
    // 1 letter, 9-10 digits.
    private static readonly Regex BoschTraceRx = new(@"[A-Z]{2}\d{4}[A-Z]\d{9,10}", RegexOptions.Compiled);
    // Bosch HW-version stamp: 14 digits + 2 letters + 4 digits (e.g.
    // 10344202406002ZC1078). Loosened to allow a single letter mid-string
    // for variants like 12683094781039S00000.
    private static readonly Regex BoschHwVerRx = new(@"\d{10,}[A-Z0-9]{4,6}[A-Z]{1,2}\d{3,5}", RegexOptions.Compiled);

    private static void ExtractFlashMetadata(byte[] d, string family,
        out string? vin, out string? hwNum, out string? hwVer,
        out string? endPn, out string? basePn)
    {
        vin = null; hwNum = null; hwVer = null; endPn = null; basePn = null;

        // VIN: scan the supplier-block region first (T43: 0x8000..0x8400;
        // E38/E67: 0xC000..0xE400). Fall back to a whole-image regex if not
        // found in the expected zone.
        vin = FindVinInRange(d, 0x8000, 0x8400)
            ?? FindVinInRange(d, 0xC000, 0xE400)
            ?? FindVin(d);

        if (family == "T43")
        {
            // Bosch trace code at 0x8298, HW-version stamp at 0x800E (4-way
            // mirrored). One match anywhere in the supplier region is fine -
            // they're identical across mirrors.
            var seg = SafeSlice(d, 0x8000, 0x400);
            if (seg.Length > 0)
            {
                var m1 = BoschTraceRx.Match(Encoding.ASCII.GetString(seg));
                if (m1.Success) hwNum = m1.Value;
                var m2 = BoschHwVerRx.Match(Encoding.ASCII.GetString(seg));
                if (m2.Success) hwVer = m2.Value;
            }

            // T43 also tags End-Model / Base-Model PN blocks with `41 4? 00`
            // header bytes followed by 8 ASCII digits. Offset 0x60009 is End
            // Model ($C1), 0x2A009 is Base Model ($C2).
            endPn = ReadBlockTaggedPn(d, 0x60009);
            basePn = ReadBlockTaggedPn(d, 0x2A009);
        }

        // E38/E67: 8-digit ASCII calibration PN at 0x1000E (the wire $C1
        // value is the 4-byte BE uint32 at 0x10005; this is the redundant
        // human-readable ASCII copy).
        if (endPn == null)
        {
            var asciiPn = SafeSlice(d, 0x1000E, 8);
            if (asciiPn.Length == 8 && asciiPn.All(b => b >= '0' && b <= '9'))
                endPn = Encoding.ASCII.GetString(asciiPn);
        }
    }

    private static string? FindVin(byte[] d) => FindVinInRange(d, 0, d.Length);

    private static string? FindVinInRange(byte[] d, int start, int length)
    {
        if (start < 0 || start >= d.Length) return null;
        int end = Math.Min(d.Length, start + length);
        // Convert just the slice to a string for regex - the bin is mostly
        // non-ASCII garbage and a whole-image string would be huge.
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
        {
            byte b = d[i];
            sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
        }
        var s = sb.ToString();
        // First pass: look for the tail+VIN descriptor. Group 2 (the VIN)
        // must end with group 1 (the tail) - that's the structural property
        // that confirms it's really a VIN block rather than a coincidental
        // 25-char run of valid VIN chars.
        foreach (Match m in VinDescriptorRx.Matches(s))
        {
            var tail = m.Groups[1].Value;
            var vin  = m.Groups[2].Value;
            if (!vin.EndsWith(tail)) continue;
            if (!IsPlausibleVin(vin)) continue;
            return vin;
        }
        // Fallback: bare VIN regex. Useful for bins where the descriptor
        // didn't land at the expected offset.
        foreach (Match m in VinRx.Matches(s))
        {
            if (IsPlausibleVin(m.Value)) return m.Value;
        }
        return null;
    }

    private static bool IsPlausibleVin(string v)
    {
        // Reject all-digits (likely filler or build-date stamp) and any
        // 17-char run with too few distinct chars (low entropy = padding).
        if (v.All(char.IsDigit)) return false;
        if (v.Distinct().Count() < 6) return false;
        return true;
    }

    private static string? ReadBlockTaggedPn(byte[] d, int tagOffset)
    {
        // Tag bytes are `41 4? 00` (ASCII 'A' + 'A'..'D' + null) immediately
        // before an 8-digit ASCII part number padded with zeros.
        if (tagOffset < 0 || tagOffset + 11 > d.Length) return null;
        if (d[tagOffset] != 0x41) return null;
        byte t2 = d[tagOffset + 1];
        if (t2 < 0x41 || t2 > 0x44) return null;
        if (d[tagOffset + 2] != 0x00) return null;
        var seg = d.AsSpan(tagOffset + 3, 8);
        for (int i = 0; i < 8; i++)
            if (seg[i] < (byte)'0' || seg[i] > (byte)'9') return null;
        return Encoding.ASCII.GetString(seg);
    }

    private static byte[] SafeSlice(byte[] d, int off, int len)
    {
        if (off < 0 || off >= d.Length) return Array.Empty<byte>();
        len = Math.Min(len, d.Length - off);
        var r = new byte[len];
        Array.Copy(d, off, r, 0, len);
        return r;
    }
}
