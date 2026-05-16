using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $1A ReadDataByIdentifier per GMW3110-2010 §8.3.
//
// USDT request:
//   byte[0] = 0x1A
//   byte[1] = dataIdentifier (DID), e.g. $90 = VIN
//
// USDT positive response (§8.3.5.1):
//   byte[0]    = 0x5A
//   byte[1]    = echoed dataIdentifier
//   bytes[2..] = identifier value (length defined per-DID by the spec)
//
// Negative responses (§8.3.5.2):
//   $7F 1A 12   SubFunctionNotSupported-InvalidFormat — request length != 2
//   $7F 1A 31   RequestOutOfRange — DID is not configured on this ECU
//
// Addressing: real GM dealer tools (DPS, TIS2WEB) issue a *functional broadcast*
// $1A $B0 to enumerate ECUs - the canonical "who's on the bus" probe documented
// on page 241 of the DPS Programmers Reference Manual:
//   "$101 $FE ... $1A $B0 — All nodes — Read Databyte Identifier $B0 —
//    Return ECU Diagnostic Address"
// Every programmable ECU must answer on its physical USDT response ID with
// "5A B0 <diag_addr>". This handler honours that by treating DID $B0 as a SPEC
// OVERRIDE: it always returns node.DiagnosticAddress regardless of what the
// config has stored at slot $B0 (the configured slot is typically used for
// vendor-specific data and would never carry the diag address itself).
//
// For any other DID arriving functionally, we stay silent rather than emitting
// an NRC - mirrors the policy used by ServiceA2/ServiceA5 to avoid blanketing
// the bus with negative responses when a single broadcast lands on every ECU.
//
// Response payload size is unbounded by the SID itself; multi-byte DIDs such
// as VIN ($90, 17 ASCII bytes) need an ISO-TP First Frame + Consecutive
// Frames. The fragmenter handles that transparently when EnqueueResponse is
// called with a >7 byte payload.
public static class Service1AHandler
{
    /// <summary>DID $B0 - "ECU Diagnostic Address" per DPS PM p.241. SPEC override:
    /// the response is always the single byte <see cref="EcuNode.DiagnosticAddress"/>,
    /// not the configured DID slot value.</summary>
    public const byte DidEcuDiagnosticAddress = 0xB0;

    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch,
                              bool isFunctional = false)
    {
        if (usdtPayload.Length != 2 || usdtPayload[0] != Service.ReadDataByIdentifier)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        byte did = usdtPayload[1];

        // DID $B0 spec override: always answer with the ECU's diag address.
        // Applies to both physical and functional addressing - the DPS PM
        // example on page 241 is functional, but real ECUs answer the same
        // way on physical too (it's the value the DID is defined to carry).
        if (did == DidEcuDiagnosticAddress)
        {
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                new byte[]
                {
                    Service.Positive(Service.ReadDataByIdentifier),
                    DidEcuDiagnosticAddress,
                    node.DiagnosticAddress,
                });
            return;
        }

        var data = node.GetIdentifier(did);
        if (data == null)
        {
            // §8.3.5.2 NRC $31 RequestOutOfRange on physical. On functional
            // broadcast, suppress so a single tester read doesn't make every
            // ECU on the link NRC simultaneously.
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByIdentifier, Nrc.RequestOutOfRange);
            return;
        }

        // For configured DIDs on functional broadcast, also stay silent -
        // the tester wouldn't expect generic vendor data to land on every
        // ECU's response ID. The $B0 spec override above is the only
        // intentionally-functional path.
        if (isFunctional)
            return;

        var resp = new byte[2 + data.Length];
        resp[0] = Service.Positive(Service.ReadDataByIdentifier);
        resp[1] = did;
        data.CopyTo(resp.AsSpan(2));

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }
}
