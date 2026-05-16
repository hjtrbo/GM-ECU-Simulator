# E38 BINARY READ.bin auto-population survey

Bin: `2011 E38 1GCRKSE36BZ158034.zip / BINARY READ.bin`
Size: 2,097,152 bytes (0x200000) - matches expected E38 full-flash readback.
VIN: `1GCRKSE36BZ158034` (2011 GMC Sierra / Silverado, 6.0L LY6).
Reported PCM Service No (`INFO.txt`): 12633238 - this is the **service replacement** PN, NOT the OS PN. The bin's OS PN is **12639835** (Module 1 in the user's list).

This report is a research pass only; no simulator code or config was modified.

---

## 1. Bin layout - what's actually in this 2 MiB

Walking the bin against the `Core/Identification/Segments/GmFamilyDefinitions.cs` scheme plus a few targeted probes:

| Region | What's there |
|---|---|
| `0x000000..0x00FFFF` | Boot / loader (sparse - mostly 0xFF padding). |
| `0x010000..0x01FFFF` | **OS module (Module 1, 12639835)**. Header at 0x10000: `A6 71 00 01 20 00 C0 DE 5B 41 41 00 00 00 "12639835" 00 00 ...`. Format is the per-module shape: `<crc16:2><module#:1><20><00 00><PN_BE:4><alpha:2><01><pad><ASCII_PN:8>`. The $1A handler for $C1 reads the 4 BE bytes at `0x10005` -> `00 C0 DE 5B` = 12,639,835. |
| `0x020000..0x09FFFF` | OS code (the dispatcher, PID table, service handlers - see section 3). |
| `0x0C0000..0x0C7FFF` | **Calibration metadata / EEPROM_DATA segment** (E38/E67 shape, base = `0xC000`). Contains the alpha-code list, module pointer table, programming date, tool stamp, VIN descriptor, trace code. Detailed map in section 2. |
| `0x100000..0x18FFFF` | OS data tables (PID lookup table at 0x145718, other constant tables). |
| `0x1C0000..0x1FFFFF` | Calibration modules 2..8 packed back-to-back (12656714, 12636563, 12620806, 12637641, 12656698 - plus the smaller cals 12625892 and 12620683 are inside the metadata region around 0xC498..0xC986). |

The boot region and unused gaps are 0xFF-padded. The five "main" cal modules each carry a header in the same shape as the OS at `0x10000` (CRC16 / module# / PN_BE at +5 / alpha at +9 / ASCII PN at +0xE). This is the canonical E38 "packed calibration" layout.

---

## 2. What `BinIdentificationReader` extracts now - confirmed against the actual bytes

### 2a. Critical bug: family auto-detection fails on this bin

`BinIdentificationReader.DetectFamily` returns **`"Unknown"`** for `BINARY READ.bin`, which silently disables the segment reader and falls back to regex-only metadata extraction.

Cause: `LooksLikeE38` requires a printable VIN descriptor at offset `0xE0AC`, but on this 2011 Silverado the descriptor is at **`0xC0AC`** (the older E38/E67 location):

```
0xC0AC: 42 5A 31 35 38 30 33 34 31 47 43 52 4B 53 45 33 36 42 5A 31 35 38 30 33 34
        |-- tail "BZ158034" --|---------- VIN "1GCRKSE36BZ158034" ----------|
0xE0AC: FF FF FF FF FF ...    (unused on this OS variant)
```

`LooksLikeE67` requires a Bosch marker. There is no `BOSCH` or `DELPHI` string anywhere in this bin (it's a Continental-supplied 6.0L Silverado E38 - 2011 vintage doesn't carry a supplier ASCII string at all). So both paths reject the bin and `DetectFamily` falls through to `"Unknown"`.

**Recommended fix (out of scope for this report, flag only)**: relax `LooksLikeE38` to accept the VIN descriptor at *either* `0xC0AC` or `0xE0AC`, and drop the `FindAscii(d, "BOSCH") < 0` veto for E38 entirely (Bosch was never the E38 supplier - Delphi was - but neither stamps ASCII supplier markers on every variant). Then disambiguate E38 from E67 by structural means (E38 OS PN format vs E67's different boot header), not by supplier-string presence.

Net effect today: the `$1A`-traced DIDs ($B0, $C1, $CB, $CC, etc.) still come through correctly because that path doesn't depend on family detection - it walks PowerPC directly. But all the segment-derived DIDs ($90 VIN, $99 ProgrammingDate, $B5 BCC, $B4 TraceCode, $C0 CalibrationPN, $C2 BaseModelPN, $28 Partial VIN) are skipped.

### 2b. EEPROM_DATA segment (would extract correctly if family detection worked)

Base `0xC000`, marker `0xA5A0` confirmed at `+0x326` -> variant with VIN anchor `+0x1CC`. All field offsets from `GmFamilyDefinitions.E38E67EepromData` hit clean values:

| Field | Offset | Value | DID it would populate |
|---|---|---|---|
| `Eeprom` (3 char tag) | `0xC007` | `"JG0"` | (informational) |
| `PCMid2` (BE u32) | `0xC020` | `12,601,203` | (informational - secondary PN) |
| `PCM` (BE u32) | `0xC028` | `12,639,900` | $C0 (Operating SW ID / cal-PN ASCII variant) |
| `TraceCode` (16 char ASCII) | `0xC02C` | `"86AATTK20237CELX"` | $B4 |
| `VIN` (17 char ASCII) | `0xC0AC + 8` | `"1GCRKSE36BZ158034"` | $90 (and $28 = `"158034"`) |

Cross-checks against `INFO.txt`: VIN matches exactly; trace code `86AATTK20237CELX` matches the user's "PCM Traceability Code" line exactly. The "PCM Service No 12633238" in `INFO.txt` is the **dealership service-replacement PN** and does not appear in the bin - the bin's actual on-the-wire calibration PN is the OS PN 12639835.

### 2c. Module / calibration ID block

The flat block at `0xC040..0xC100` is a structured directory of all 8 calibration modules:

```
0xC040: 41 42 41 42 41 4C 41 43 41 42 FF FF              "ABABALACAB" - alpha codes for modules 1..8 (plus padding)
0xC04C: 00 C1 20 4A  00 C0 D1 93  00 C0 94 06  00 C0 D5 C9        \
0xC05C: 00 C1 20 3A  00 C0 DE 5B  41 41 FF FF FF FF FF 00 00 FF    > module #s as BE u32 (cross-reference with alphas above)
0xC080: 20 18 09 13                                                BCD programming date "20180913" (13 Sep 2018)
0xC0A0: 31 38 32 33 30 37 30 37 34 00                              programming tool stamp "182307074"
0xC0AC: 42 5A 31 35 38 30 33 34 31 47 43 52 4B 53 45 33 36 42 5A 31 35 38 30 33 34   tail+VIN descriptor
```

The alpha-codes list maps 1:1 to the user's module list: M1=AB, M2=AB, M3=AL, M4=AC, M5=AB (note: the alpha list shown here is in module-number order, NOT the "PCM Module 1..8" labelling order from INFO.txt, which is purely a UI numbering convention).

Programming date (`20180913` BCD) is what $99 ReadDataByIdentifier would return on the wire. Tool stamp `"182307074"` is what $9A (programming tool ID) would return.

### 2d. Per-module headers

Each of the 8 cal modules carries a self-describing header. Confirmed offsets (top-of-module + standard layout):

| Module | Offset | PN_BE @+5 | Alpha @+9 | ASCII PN @+E |
|---|---|---|---|---|
| 1 OS | 0x010000 | 12,639,835 | "AA" | "12639835" |
| 2    | 0x1C0000 | 12,656,714 | "AB" | "12656714" |
| 3    | 0x1C1400 | 12,636,563 | "AB" | "12636563" |
| 4    | 0x1C36C0 | 12,620,806 | "AL" | "12620806" |
| 5    | 0x1C39F0 | 12,637,641 | "AC" | "12637641" |
| 6    | 0x1CD0D0 | 12,656,698 | "AB" | "12656698" |
| 7    | 0x00C498 (inside meta) | 12,625,892 | -- | "12625892" |
| 8    | 0x00C4A8 (inside meta) | 12,620,683 | -- | "12620683" |

The $1A dispatcher in this bin almost certainly routes $C1..$C7 (8 module PNs); this gives the simulator a complete set of DID values for the "8 calibration IDs" segment of the diagnostic identification suite.

---

## 3. The big find: the OS exposes a complete `$22` PID definition table in flash

At file offset **`0x145718`** there is an 8-byte-record lookup table with the following structure:

```
byte 0:    record-type flags (mostly 0x01, some 0x02/0x04, plus single 0x07/0x0D entries)
byte 1:    0x00 (alignment/padding)
bytes 2-3: PID number (BE uint16)
bytes 4-5: data length in bytes (BE uint16, range 5..27 observed)
bytes 6-7: low half of the data pointer (BE uint16)
```

The table runs `0x145718..0x1467D8` for **536 records**, monotonically sorted by PID number from `0x0001` to `0xD8B5`. Spot-checks:

- PID `0x155B` (the one the user observed NRC'ing): record at `0x146080` = `01 00 15 5B 00 11 F2 68` -> **size 17 bytes**, ptr_lo `0xF268`.
- PID `0x0001` (first): size 8 bytes, ptr_lo `0xE154`.
- PID `0xD8B5` (last): size 25 bytes, ptr_lo `0x08AC`.
- Size histogram is broad: 6-byte (41 records), 19-byte (47), 22-byte (38), 13-byte (35) are the most common buckets. Nothing exotic.

This is structurally what the $22 dispatcher consults at request time. **Auto-extracting this table gives the simulator the complete list of PIDs the real ECU supports plus their wire-response sizes.**

### What's missing: the *values*

The 16-bit `ptr_lo` is only the low half of a 32-bit pointer. The high half is loaded at runtime by a `lis r12, <hi>` instruction inside the per-record dispatch helper, and that high half determines the memory bank: chip RAM (`0x40xxxxxx` typical for an MPC555x core), peripheral registers (`0xFFFxxxxx`), or flash (`0x000xxxxx` / `0x001xxxxx`). The `byte 0` record-type field very likely *is* the bank selector (1=RAM, 2=RAM-other, 4=peripheral, 7/13=special-cased) but proving that mapping needs a disassembler pass over the $22 handler.

So for each PID we can recover **PID number + wire size**, but **NOT the actual response bytes** without either:
- a PowerPC disassembly of the $22 handler (cleanly tractable but a 1-2 day job per family), OR
- a vendor-doc / public RE reference that already tables out the type-byte semantics for E38.

For the post-flash configuration flow specifically, the PIDs the DPS utility file queries are mostly *live sensor readings* (which the simulator should drive with waveforms anyway) and *calibration constants* (a subset of which DO live in flash, but identifying which is which requires the disassembly above).

---

## 4. What can be reliably extracted right now (with offsets and values)

For this specific bin, assuming the family-detection bug in section 2a is fixed:

| Wire DID | Value | Source in bin |
|---|---|---|
| `$28` Partial VIN | `"158034"` | last 6 of VIN at 0xC0B4 |
| `$90` VIN | `"1GCRKSE36BZ158034"` | 0xC0B4 (17 bytes) |
| `$99` Programming date | `20180913` (BCD `20 18 09 13`) | 0xC080 |
| `$9A` Programming tool | `"182307074"` | 0xC0A0 (10 bytes) |
| `$B0` Diagnostic addr / programmed state | `00 C1 21 2E` (4 BE) | $1A trace -> 0x10005 mirror, currently maps to the cal PN. Worth re-checking the $1A handler routing for $B0; on the 2016 LSA bin in `config.json` it ended up equal to $C1. |
| `$B4` Mfg traceability | `"86AATTK20237CELX"` | 0xC02C (16 bytes) |
| `$B5` Broadcast code | `"9835"` | last 4 of "12639835" |
| `$C0` Operating SW ID | `"12639835"` (or `"12639900"` from segment - the two PNs differ; the $1A wire value is the OS PN) | OS header ASCII at 0x1000E |
| `$C1` End-model PN (wire bytes) | `00 C0 DE 5B` (= 12,639,835) | $1A trace -> 0x10005 |
| `$C2..$C7` Module PNs (BE u32) | 12656714, 12636563, 12620806, 12637641, 12656698 | Module header `+5` at each cal-block base |
| `$D1..$D7` Alpha codes (2 ASCII each) | "AB","AB","AL","AC","AB","FF" (M6 alpha is 0xFFFF -> skip) | Module header `+9` at each cal-block base; existing `SynthesiseAlphaCodeDids` already handles this once $C1..$C7 are traced as `FlashUInt32BE` |
| **Supported PID list** (536 PIDs with size) | full table starting at 0x145718 | new - not yet consumed by reader |

The first 11 rows are values the existing `BinIdentificationReader` is already designed to produce; the family-detection fix is the only thing standing between this bin and a clean extraction matching the precedent set by the existing `config.json` (which was extracted from a different E38).

The 536-PID table is the genuinely new finding.

---

## 5. What is NOT extractable without further work

- **PID response *bytes*** for any of the 536 PIDs. We get number + size for free; the actual values require disassembling the $22 handler to resolve the high half of each pointer, then reading from the correct address class (and even then, only the PIDs whose data is calibration-constant rather than runtime-sensor-driven are usefully readable from flash). Out of scope for a half-day research task; comfortably tractable for someone who's willing to spend 1-2 days with a PowerPC disassembler.
- **$54 CHANGE_DATA / $53 COMPARE_DATA expected payloads.** These ops in the DPS utility file reference calibration constants by RAM address - the simulator can only "match" them if it can produce the expected calibration bytes at $22 read time. Same blocker as above (high-half pointer resolution).
- **$3B CorporateDID writes** to corporate DID slots. The simulator already supports configurable DID writability per the persistence schema; what's missing is knowing which DIDs the post-flash flow writes to. Mining the *DPS utility file* (not the bin) is the right source for that, and `tools/dps_utility_builder/parse_utility_file.py` already targets exactly that artifact.
- **PID semantics / units / waveform shapes.** The bin gives us "PID 0x155B is 17 bytes" but says nothing about what those 17 bytes mean. For PIDs in the OBD-II / SAE J1979 / GMW-public ranges (0x0001..0x00FF and some 0x11XX ranges) the meanings are well documented; for OEM-private PIDs the meanings come from GM-internal docs or community RE.

---

## 6. Recommendation - feasibility verdict

**Hybrid is the right answer**, with three concrete steps that should be done in order:

### Step 1 (one-day fix): repair family detection + harvest the 11 identification DIDs

The family-detection bug in section 2a is a real defect against any pre-2014-ish E38 readback. Fixing it costs maybe 30 lines in `BinIdentificationReader.cs` plus a unit test that adds this bin's bytes (or a 1 KiB excerpt of the EEPROM_DATA region) as a test fixture. Result: the user gets a fully populated identifier panel for this and every similar bin.

This alone moves the simulator from "user must hand-type each DID" to "drop in a bin, get accurate $1A responses" for all the corporate identification DIDs. Independently valuable.

### Step 2 (small feature): auto-create empty PID definitions for every PID in the bin's $22 table

Add a one-shot bin-import step that walks the 8-byte-record table at `0x145718` and emits a `Pids[]` array entry per record with:
- `address` = the PID number (`pid` field, e.g. `0x155B`)
- `size` = a size hint propagated to the response generator (the existing schema's `size` enum is byte/word/dword; we'd want a new `lengthBytes` int for sizes >4, or generate `size: byte` with a `repeatCount` of N)
- `name` = `"PID 0x155B (auto)"`
- `waveform` = a constant-0 or sine-low-amplitude placeholder

Net effect: the simulator stops NRC'ing $31 RequestOutOfRange for any PID the real ECU supports. The values are wrong (they're flat zeros or a placeholder sine), but **the wire shape is correct**, which is exactly what a DPS configuration session needs to make progress through the flow. The session won't stall on "this PID doesn't exist"; if a later $54/$53 op compares the response against a specific value, *that* op will fail at its own step, and the user can fix one PID at a time rather than fighting the dispatcher itself.

The table walker is ~30 lines of C# - the structure is rigid and unambiguous. It does NOT depend on the family-detection fix (the table location is found by signature, not by family lookup).

### Step 3 (deferred, larger): real value extraction via $22 handler disassembly

Only worth doing if Step 2 turns out to be insufficient (i.e. the user finds that the DPS utility file genuinely requires specific PID values for $53 COMPARE_DATA ops to succeed and the placeholders aren't enough). At that point the work is:

1. Locate the $22 handler entry (same trampoline pattern the existing reader uses for $1A).
2. Find the dispatcher loop that fetches a record from the table and resolves the full pointer.
3. Disassemble the bank-selector switch on `byte 0` to get the high-half-by-type mapping.
4. For each PID, compute the full address, and emit:
   - `address` (full 32-bit), `lengthBytes`, raw bytes from flash if the high half is in `0x00xxxxxx`/`0x01xxxxxx`, else mark `runtime-only` and leave as placeholder.

Estimate 1-2 days with Ghidra or IDA + the PowerPC architecture plugin, longer without.

### Honest verdict

For the user's stated goal ("make the post-flash configuration phase just work"), **Step 1 + Step 2 will get you 80-90% of the way there for ~1 day of work.** Most $22 reads will succeed (correct size, placeholder value); a handful of them - the $53 COMPARE_DATA targets specifically - will probably still fail, but they'll fail one-at-a-time with a clear "expected X got Y" signature, and the user can fill in the right value in `config.json` by hand or via a $22 sniff from a real ECU.

**Step 3 (full disassembly) is not worth doing speculatively.** Wait until Step 2 is shipped and the user has identified which specific PIDs still need real values; the disassembly effort can then be focused on those.

---

## 7. Sketch of the Python script (for Step 2)

A ~40-line Python script would emit the JSON snippet for direct paste into `config.json -> ecus[0].pids[]`:

```python
import struct, json, sys

BIN = sys.argv[1]
with open(BIN, 'rb') as f: d = f.read()

def is_rec(o):
    if o < 0 or o + 8 > len(d) or d[o+1] != 0:
        return False
    rt = d[o]
    pid = (d[o+2] << 8) | d[o+3]
    sz  = (d[o+4] << 8) | d[o+5]
    return 1 <= rt <= 0x20 and 1 <= pid <= 0xFFFF and 1 <= sz <= 0x100

# Anchor at a known interior offset of the table (0x146080 = PID 0x155B record
# on this 2011 Silverado bin; offsets for the same OS family will sit nearby).
# Walk back to find the head, then forward to the tail.
anchor = 0x146080
while is_rec(anchor - 8): anchor -= 8
recs = []
o = anchor
while is_rec(o):
    recs.append((d[o], struct.unpack('>H', d[o+2:o+4])[0],
                       struct.unpack('>H', d[o+4:o+6])[0]))
    o += 8

pids = [{
    "address": f"0x{pid:04X}",
    "name":    f"PID 0x{pid:04X} (auto, {sz} bytes)",
    "size":    "byte" if sz == 1 else ("word" if sz == 2 else "dword"),
    "lengthBytes": sz,                    # placeholder until schema supports it
    "dataType": "unsigned",
    "scalar":   0,
    "offset":   0,
    "unit":     "",
    "waveform": { "shape": "constant", "amplitude": 0, "offset": 0,
                  "frequencyHz": 0, "phaseDeg": 0, "dutyCycle": 0.5 }
} for rt, pid, sz in recs]

print(json.dumps(pids, indent=2))
```

For locating the table on bins from other E38 OS revisions, the anchor heuristic should be "scan for the densest run of monotonically-increasing 16-bit fields at offset+2 of 8-byte records with byte 0 in {01, 02, 04}". The 0x145718 base will drift between OS versions but the signature is unmistakable - 500+ consecutive valid records is a one-in-a-trillion accident.

---

## 8. Caveats and uncertainty

- The 8-byte record structure was confirmed empirically against one bin. I have not cross-checked against a second E38 readback - the field positions and byte-0 semantics could in principle differ between E38 OS revisions. If introducing the auto-PID-population feature, gate it on detecting the table signature (the 500+ monotonically-increasing PID anchor) rather than hardcoding the offset.
- The `byte 0` flags field is **hypothesised** to be the memory-bank selector but is **unverified**. It might also encode "is this PID security-locked", "is this readable in default session", "scaling exponent", etc. Step 3 (disassembly) is the only way to know for certain.
- I did not run the simulator or the existing `BinIdentificationReader` directly against `BINARY READ.bin` (the user asked for a research-only pass). All the "what would the reader produce" statements in section 2 are inferred by reading the C# code and probing the bytes; if the simulator's actual behaviour on this bin differs from what's predicted in section 4, that's worth investigating before committing to the Step 1 fix.
- This bin appears to have been pulled by an aftermarket reader (size matches the chip exactly, no zero-pad at the end, programming-date stamp `20180913` is the user's own programming session). It is therefore a *valid* readback, not a partial or interleaved one - the offsets used above should generalise to anyone else's stock E38 read on the same 6.0L truck calibration family.

---

## Appendix: provenance and where the source files live

- Bin extracted to: `C:/Users/Nathan/.claude/projects/C--Users-Nathan-OneDrive-ECA-Resources-Visual-Studio-GM-ECU-Simulator/agent_e38_extract/BINARY READ.bin`
- Existing simulator reader: `Core/Identification/BinIdentificationReader.cs`
- Existing segment definitions: `Core/Identification/Segments/GmFamilyDefinitions.cs`
- Existing applier (writes extracted DIDs into NodeState): `Core/Identification/BinIdentificationApplier.cs`
- Current config (with auto-extracted DIDs from a *different* 2016 LSA bin, not this one): `C:/Users/Nathan/AppData/Local/GmEcuSimulator/config.json`
