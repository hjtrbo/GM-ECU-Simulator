using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Transport;

namespace Core.Services;

// $20 ReturnToNormalMode. Per GMW3110 §8.5.6.2:
//   - Calls Exit_Diagnostic_Services() — clears all enhanced state.
//   - Length != 1 -> $7F $20 $12.
//   - Otherwise: $60 positive response.
public static class Service20Handler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, DpidScheduler scheduler)
    {
        if (usdtPayload.Length != 1 || usdtPayload[0] != Service.ReturnToNormalMode)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ReturnToNormalMode, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        EcuExitLogic.Run(node, scheduler, ch);
    }
}
