using Common.Protocol;

namespace Core.Ecu;

// Gives a freshly-created EcuSimulator ECU a baseline identity so a tester can query it the instant it appears on the
// bus. Without this a brand-new ECU NRC-$31s every $1A read and looks dead to discovery tools that probe for a VIN.
//
// Each seeded DID is materialised as a Mode1A StaticBytes Pid row rather than an entry in EcuNode's identifier
// dictionary. That choice buys three things for free: the row persists through the v15 PidDto/StaticBytes path in
// ecu_simulator.mode.json with no schema change, it shows up in the editor PID grid as a visible, editable row, and
// it is the same row Service3BHandler's write-through updates so a tester's $3B VIN write survives a save/load.
//
// Call this only on genuine new-ECU creation in EcuSimulator mode - the AddEcu button and the first-launch
// DefaultEcuConfig. DPS-primed ECUs derive their identity from the archive / donor bin and must never be seeded with
// these synthetic placeholders; the IsPrimed guard below enforces that even if a future caller forgets the rule.
public static class EcuIdentitySeeder
{
    // The DIDs a brand-new ECU is born with - a curated E38/E67-realistic $1A identity set so a blank ECU presents the
    // same kind of identity block a real Gen-IV/V GM controller answers, not just a lone VIN. Every entry must have a
    // DefaultDidValues.Get placeholder (Seed skips any that return null). Membership mirrors what a stock E38/E67
    // readback surfaces (VIN + partial VIN, supplier/system ids, programming date, ECU config, enable counter, diag
    // address, traceability + broadcast code, the operating-software / model part numbers, and the boot + cal-1 SW
    // alpha codes); grow it as needed.
    public static readonly byte[] SeededDids =
    {
        0x90,  // VIN
        0x28,  // Partial VIN (last 6 of VIN)
        0x92,  // System Supplier ID
        0x97,  // System Name / Engine Type
        0x98,  // Repair Shop Code / SN
        0x99,  // Programming Date
        0x9B,  // ECU Configuration / Coding
        0xA0,  // Manufacturers Enable Counter
        0xB0,  // Diagnostic Address
        0xB4,  // Mfg Traceability Chars
        0xB5,  // Broadcast Code
        0xC0,  // Operating Software ID
        0xC1,  // End Model Part Number
        0xC2,  // Base Model Part Number
        0xCB,  // End Model Number
        0xCC,  // Base Model Number
        0xD0,  // Boot SW Alpha Code
        0xD1,  // SW Alpha Code 1
    };

    // Seeds every entry in SeededDids the ECU does not already carry. Existing Mode1A rows are left untouched, so this
    // is precedence-safe against a loaded config and is a no-op on a re-seed.
    public static void Seed(EcuNode node)
    {
        // Primed ECUs own their identity from the archive - never overwrite it with a synthetic placeholder.
        if (node.IsPrimed) return;

        foreach (var did in SeededDids)
        {
            // A DID already present (loaded-config row, an earlier seed) wins over the synthetic default.
            if (node.GetMode1APid(did) != null) continue;

            var value = DefaultDidValues.Get(did);
            if (value is null || value.Length == 0) continue;

            // Size is informational once LengthBytes is set; DWord matches how PidCatalogue tags any >4-byte row.
            node.AddPid(new Pid
            {
                Mode        = PidMode.Mode1A,
                Address     = did,
                Name        = Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                LengthBytes = value.Length,
                StaticBytes = value,
                Size        = PidSize.DWord,
                DataType    = PidDataType.Unsigned,
            });
        }
    }
}
