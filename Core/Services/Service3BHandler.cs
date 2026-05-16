using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// <summary>
// GMW3110-2010 Service $3B WriteDataByIdentifier (§8.14, p.170-173).
// Tester flow: DPS SPS Interpreter Programmers Reference Manual, Op-Code $3B
// "Mode 3B Write Data by Identifier", GMLAN Interpreter 3 section (p.167);
// exercised by Appendix D steps $11/$12/$13 (DIDs $90 VIN, $98 TesterSerialNumber,
// $99 SystemProgrammingDate). Positive response shape per the spec pseudo code
// in §8.14.6.2 and the DPS pseudo code table on p.167:
//     [$7B, did]
// - the dataRecord is NOT echoed (only the DID byte is). §8.14.5.2 Table 151
// shows the wire example: an FF/CF VIN write returns a Single Frame
// "$02 $7B $90" - 2 payload bytes, no echoed VIN data.
// </summary>
//
// USDT request shape (§8.14.2 Table 146):
//   byte[0]    = $3B SID
//   byte[1]    = dataIdentifier (DID)
//   byte[2..]  = dataRecord (length defined per-DID by Appendix C)
//
// NRC matrix per §8.14.4 Table 150:
//   $12 SFNS-IF   - request shorter than 3 bytes (no DID, or no data after DID)
//                   OR dataRecord length does not match the expected length
//                   for this DID. §8.14.4 §8.14.6.2 first ELSE-IF: the
//                   message_data_length - 2 != expected_length check.
//   $22 CNC-RSE   - "operating conditions of the ECU are such that it cannot
//                   perform the required action (e.g. EEPROM failure)". Not
//                   simulated here - we have no EEPROM-failure state to model.
//   $31 ROOR      - covers THREE distinct conditions per Table 150:
//                     (a) DID not supported on this ECU
//                     (b) DID is read-only via $1A
//                     (c) DID is secured AND ECU is not in an unlocked state
//                     (d) data bytes are invalid for this DID
//                   This is why security-locked writes return $31 here, NOT $33
//                   SecurityAccessDenied: §8.14.4 doesn't list $33 at all -
//                   the GMW3110 author chose to fold security under ROOR for $3B
//                   (in contrast to $27 SecurityAccess which uses $33 properly
//                   for its own sub-function gating). $33 is also absent from
//                   the §8.14.6.2 pseudo code.
//   $78 RCR-RP    - "writing to address takes more than P2C ms" - not
//                   simulated (writes are instantaneous in-memory mutations).
//
// Per-DID semantics implemented:
//   $90 VIN                       17 ASCII bytes (Appendix C). Persists to
//                                 EcuNode.Identifiers[$90] so a subsequent
//                                 $1A $90 read returns the new value. Requires
//                                 security unlock (any level > 0); otherwise
//                                 NRC $31 per Table 150 condition (c).
//   $98 RepairShopCodeOrTesterSerialNumber
//                                 10 ASCII bytes per Appendix C Table (line
//                                 listed as "R/W ASCII 10"). The ASCII identity
//                                 of the tool that programmed the ECU.
//                                 Persists to Identifiers[$98]. Requires
//                                 security unlock.
//   $99 ProgrammingDate           4 bytes BCD per Appendix C (listed as
//                                 "R/W BCD 4") - typically YY YY MM DD packed
//                                 to keep a full century. Persists to
//                                 Identifiers[$99]. Requires security unlock.
//   Any other DID                 NRC $31 ROOR. Spec §8.14.1: "An ECU is not
//                                 required to support all corporate standard
//                                 DIDs". We don't silently accept arbitrary
//                                 writes; a tester wanting to write a vendor-
//                                 specific DID needs to add it to this table
//                                 with its declared length and security
//                                 attributes.
//
// Persistence semantics: writes are runtime-only mutations on EcuNode. We do
// NOT write back to any source .bin file or persist across simulator restarts
// (per the memory note `feedback_bins_are_upstream_immutable`: source bins are
// upstream-immutable; runtime state is the right place for live overrides).
// File -> Save in the UI will round-trip the new values through ConfigSchema
// because Identifiers is already a save-tracked property.
public static class Service3BHandler
{
    // Per-DID write descriptor: expected length on the wire, and whether
    // security unlock at any level > 0 is required. Keep this table next to
    // the handler so adding a new writable DID (e.g. an OEM-specific identifier
    // documented in a CTS) is a one-line change with the spec's rule co-located.
    private readonly record struct DidWriteRule(int ExpectedLength, bool RequiresSecurity);

    // Length 0 means "any non-zero length is accepted" - useful for variable-
    // length ASCII identifiers like the tester serial number where the spec
    // doesn't fix a length.
    private static readonly Dictionary<byte, DidWriteRule> WritableDids = new()
    {
        // §8.14.5.1 worked example shows 17-byte VIN write; Appendix C confirms
        // VIN is 17 ASCII bytes.
        [0x90] = new(ExpectedLength: 17, RequiresSecurity: true),

        // RepairShopCodeOrTesterSerialNumber. Appendix C entry: "R/W ASCII 10"
        // - fixed 10 ASCII bytes.
        [0x98] = new(ExpectedLength: 10, RequiresSecurity: true),

        // ProgrammingDate. Appendix C entry: "R/W BCD 4" - packed BCD,
        // typically YY YY MM DD (full-century encoding). Fixed 4 bytes.
        [0x99] = new(ExpectedLength: 4, RequiresSecurity: true),
    };

    /// <summary>
    /// Handles a $3B WriteDataByIdentifier request. Returns true on positive
    /// response so the persona's dispatch can refresh P3C (a successful $3B is
    /// enhanced traffic - it gates the SPS Appendix D Part 2 flow).
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        // Sanity guard: the persona dispatches on byte[0] so this should always
        // hold, but assert it so an accidental rewire surfaces as NRC $12 on
        // the wire rather than a malformed positive response.
        if (usdtPayload.Length < 1 || usdtPayload[0] != Service.WriteDataByIdentifier)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.WriteDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // §8.14.6.2 pseudo code "message_data_length < 3" branch: request must
        // carry at least SID + DID + 1 data byte. A bare [$3B] or [$3B did]
        // with no payload is malformed.
        if (usdtPayload.Length < 3)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.WriteDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte did = usdtPayload[1];
        int dataLength = usdtPayload.Length - 2;

        // §8.14.6.2 first IF: DID not supported -> $31.
        if (!WritableDids.TryGetValue(did, out var rule))
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.WriteDataByIdentifier, Nrc.RequestOutOfRange);
            return false;
        }

        // §8.14.6.2 second ELSE-IF: data length mismatch -> $12. Every
        // currently-defined writable DID has a fixed length per Appendix C.
        if (dataLength != rule.ExpectedLength)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.WriteDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // §8.14.6.2 third ELSE-IF: DID requires security access AND ECU is
        // locked -> $31 ROOR (Table 150 condition (c)). Note this is NOT $33
        // SecurityAccessDenied - $3B folds security failures under ROOR.
        // node.State.SecurityUnlockedLevel == 0 is the spec's "Security_
        // Access_Unlocked = FALSE" state.
        if (rule.RequiresSecurity && node.State.SecurityUnlockedLevel == 0)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.WriteDataByIdentifier, Nrc.RequestOutOfRange);
            return false;
        }

        // Write data values from dataRecord[] to the memory address associated
        // with the DID. EcuNode.SetIdentifier mirrors the spec's "memory
        // address associated with the dataIdentifier" abstraction - the
        // $1A read handler returns whatever bytes we store here, so a
        // subsequent $1A $90 round-trips the new VIN.
        var data = usdtPayload.Slice(2).ToArray();
        node.SetIdentifier(did, data);

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.WriteDataByIdentifier), did]);
        return true;
    }
}
