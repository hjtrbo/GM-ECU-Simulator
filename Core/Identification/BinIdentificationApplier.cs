using System.Text;
using Common.Protocol;
using Core.Ecu;

namespace Core.Identification;

/// <summary>
/// Applies a <see cref="BinIdentificationReader.BinIdentification"/> result to
/// an <see cref="EcuNode"/>'s $1A identifier map. Lives in Core (not the WPF
/// view-model) so it can be unit-tested without dragging in WPF or a file
/// picker; <c>EcuViewModel.LoadInfoFromBin</c> calls it after asking the user
/// which load mode they want.
///
/// <para>Two modes are supported:</para>
/// <list type="bullet">
///   <item>
///     <description><see cref="LoadMode.Merge"/> - keep DIDs already set on
///     the model; only fill the ones currently blank. Layered precedence:
///     user hand-edits and a prior auto-populate take precedence over the
///     bin extraction.</description>
///   </item>
///   <item>
///     <description><see cref="LoadMode.ReplaceAll"/> - clear every existing
///     DID on the ECU first, then write whatever the bin produced. DIDs not
///     extractable from the bin are left blank.</description>
///   </item>
/// </list>
/// </summary>
public static class BinIdentificationApplier
{
    public enum LoadMode
    {
        /// <summary>
        /// Keep DIDs already set on the ECU. Bin values only fill blanks.
        /// </summary>
        Merge,

        /// <summary>
        /// Clear every DID on the ECU first, then write only what the bin
        /// surfaced. Anything not in the bin ends up unconfigured.
        /// </summary>
        ReplaceAll,
    }

    /// <summary>
    /// Result summary: which DID labels got written, and which were kept
    /// because they were already populated (only meaningful in
    /// <see cref="LoadMode.Merge"/>).
    /// </summary>
    public sealed record ApplyOutcome(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

    public static ApplyOutcome Apply(EcuNode node, BinIdentificationReader.BinIdentification result, LoadMode mode)
    {
        var applied = new List<string>();
        var skipped = new List<string>();

        // ReplaceAll: drop every existing DID up front so anything the bin
        // doesn't surface ends up unconfigured (the user explicitly asked
        // for a clean slate from the dialog). Source tags are wiped at the
        // same time so the sticky "user blanked this" flag from prior edits
        // doesn't outlive the Replace-all reset; every well-known DID is
        // re-marked as Bin source at the end of this method.
        if (mode == LoadMode.ReplaceAll)
        {
            foreach (var did in node.Identifiers.Keys.ToArray())
                node.RemoveIdentifier(did);
            node.ClearAllIdentifierSources();
        }

        bool HasIdentifier(byte did)
        {
            var bytes = node.GetIdentifier(did);
            return bytes != null && bytes.Length > 0;
        }

        bool TryWrite(byte did, byte[] bytes, string label)
        {
            if (bytes.Length == 0) return false;
            // Merge mode honours precedence: user hand-edits and prior auto-
            // populate values (sticky source=User or source=Auto with bytes
            // present) override the bin extraction. ReplaceAll already wiped
            // sources above, so no skip logic needed there.
            if (mode == LoadMode.Merge && HasIdentifier(did))
            {
                skipped.Add(label);
                return false;
            }
            node.SetIdentifier(did, bytes, Common.Protocol.DidSource.Bin);
            applied.Add(label);
            return true;
        }

        bool TryWriteAscii(byte did, string? s, string label)
            => !string.IsNullOrEmpty(s) && TryWrite(did, Encoding.ASCII.GetBytes(s!), label);

        var alreadyHandled = new HashSet<byte>();

        // Walk every DID the parser surfaced. Sources can be:
        //   - dispatcher-traced ($1A wire path): WireBytes is the on-wire
        //     payload (RuntimeComputed entries have empty WireBytes because
        //     the value lives in chip RAM populated at runtime; we skip
        //     those because we don't know what to write)
        //   - segment-derived (EEPROM / flash markers): WireBytes is the
        //     ASCII rendering of the structural value
        // Either way, the rule is: if WireBytes is non-empty, write it.
        // The label is the GMW3110 §8.3.2 name from Gmw3110DidNames so the
        // outcome panel reads "$90 VIN" rather than just "$90".
        foreach (var did in result.Dids.OrderBy(x => x.Did))
        {
            if (did.WireBytes.Length == 0) continue;
            var label = $"${did.Did:X2} {Gmw3110DidNames.NameOf(did.Did) ?? "(unknown)"}";
            if (TryWrite(did.Did, did.WireBytes, label) || mode == LoadMode.Merge)
                alreadyHandled.Add(did.Did);
        }

        bool TryWriteAsciiUnlessHandled(byte did, string? s, string label)
            => !alreadyHandled.Contains(did) && TryWriteAscii(did, s, label);

        // Top-level structural fields on BinIdentification. The parser's
        // SynthesiseSegmentDerivedDids step normally promotes most of these
        // into result.Dids, but a caller constructing BinIdentification
        // directly (e.g. tests, future loaders that bypass the PowerPC
        // walker) can still rely on these fields landing on the right DIDs.
        // Each call no-ops when the corresponding DID was already written
        // out of result.Dids above, so there's no double-write risk.
        TryWriteAsciiUnlessHandled(0x90, result.Vin,                     "$90 VIN");
        TryWriteAsciiUnlessHandled(0x92, result.SupplierHardwareNumber,  "$92 System Supplier ID");
        TryWriteAsciiUnlessHandled(0x98, result.SupplierHardwareVersion, "$98 Repair Shop Code / SN");
        TryWriteAsciiUnlessHandled(0x99, result.ProgrammingDate,         "$99 Programming Date");
        TryWriteAsciiUnlessHandled(0xB4, result.TraceCode,               "$B4 Mfg Traceability Chars");
        TryWriteAsciiUnlessHandled(0xB5, result.BroadcastCode,           "$B5 Broadcast Code");
        TryWriteAsciiUnlessHandled(0xC2, result.BaseModelPartNumber,     "$C2 Base Model Part Number");

        // ReplaceAll postcondition: every well-known DID is sourced from this
        // bin load, even the ones the bin didn't surface a value for. That's
        // the user's explicit intent when they chose Replace-all - "this
        // ECU's identifier set is the bin's view, full stop". Blank Bin-tagged
        // rows show as source=bin in the grid so it's obvious the bin simply
        // didn't have a value for them.
        if (mode == LoadMode.ReplaceAll)
        {
            foreach (var did in Gmw3110DidNames.KnownDids)
            {
                if (node.GetIdentifierSource(did) == Common.Protocol.DidSource.Bin) continue;
                node.SetIdentifierSource(did, Common.Protocol.DidSource.Bin);
            }
        }

        return new ApplyOutcome(applied, skipped);
    }
}
