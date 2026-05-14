using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $10 InitiateDiagnosticOperation — accepted as a no-op per the user's
// scope decision. We don't model the various sub-functions ($02 disable
// DTCs, $03 enable DTCs in DeviceControl, $04 wakeUpLinks, etc.); we just
// echo the sub-function and activate the P3C timer so subsequent $3E logic
// behaves correctly.
public static class Service10Handler
{
    /// <summary>Returns true if a positive response was enqueued (caller should
    /// activate P3C). False if an NRC was enqueued.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        if (usdtPayload.Length != 2 || usdtPayload[0] != Service.InitiateDiagnosticOperation)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.InitiateDiagnosticOperation, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        byte sub = usdtPayload[1];

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.InitiateDiagnosticOperation), sub]);
        return true;
    }
}
