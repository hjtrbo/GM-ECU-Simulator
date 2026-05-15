using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services.Uds;

// $31 RoutineControl per ISO 14229-1:2020 §11.7. NOT a GMW3110-2010 service -
// only reachable when an EcuNode's Persona is UdsKernelPersona, which the
// simulator activates after a successful $36 sub $80 DownloadAndExecute hands
// the bus to a GM SPS programming kernel (powerpcm_flasher etc.). Baseline
// GMW3110 ECUs answer $31 with NRC $11 ServiceNotSupported via the
// IDiagnosticPersona default-false return path.
//
// Wire format (ISO 14229 §11.7.2 Table 421):
//   Request:  [$31][sub][routineId hi][routineId lo][optionRecord 0..n]
//             sub-function: $01 startRoutine, $02 stopRoutine, $03 requestRoutineResults
//             routineIdentifier: 2-byte big-endian
//             routineControlOptionRecord: routine-specific bytes
//   Positive: [$71][sub][routineId hi][routineId lo][statusRecord 0..n]
//
// Two routines are observed from powerpcm_flasher (and other GM SPS tools):
//   $FF00 Erase Memory by Address - options = startAddr(4 BE) + size(4 BE)
//   $0401 Check Memory by Address - options = startAddr(4 BE) + size(4 BE)
// Real GM kernels respond with the routineId echoed plus a 1-byte status
// (00 = completed OK). powerpcm parses any $71 with the right sub+id as
// success and moves on. We answer all known and unknown routines with
// statusRecord = $00 (completed) since the simulator doesn't model a
// persistent flash store - extending this to compute a real CRC over the
// $36 sink buffer is straightforward when the user wants it.
//
// NRCs:
//   $12 SFNS-IF - length too short, or sub-function outside {$01,$02,$03}
//   $33 SAD    - security not unlocked (kernel routines are post-$27)
//
// Side effects (capture mode only):
//   $01 startRoutine + routineId $FF00 + 8-byte optionRecord
//   (4-byte startAddress BE + 4-byte size BE) records a
//   <see cref="Core.Ecu.FlashEraseRegion"/> on the node. Subsequent $36 writes
//   that land in [start, start+size) are mirrored into the region's $FF-filled
//   buffer; BootloaderCaptureWriter dumps one .bin per region at session end.
public static class Service31Handler
{
    public const byte StartRoutine = 0x01;
    public const byte StopRoutine = 0x02;
    public const byte RequestRoutineResults = 0x03;

    /// <summary>Routine ID for "Erase Memory by Address" used by GM SPS kernels.</summary>
    public const ushort RoutineIdEraseMemoryByAddress = 0xFF00;

    /// <summary>Resource cap on a single erase region. 16 MiB covers any realistic
    /// GMW3110 calibration / OS section and prevents a malformed kernel request
    /// (or a hostile tool) from triggering a multi-GB allocation.</summary>
    public const uint MaxFlashEraseRegionBytes = 16 * 1024 * 1024;

    /// <summary>Returns true if a positive response was sent (caller activates P3C),
    /// false if an NRC was sent.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        const byte sid = Iso14229.Service.RoutineControl;

        // §11.7.2: minimum length is SID + sub + 2-byte routineIdentifier.
        if (usdtPayload.Length < 4 || usdtPayload[0] != sid)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte sub = usdtPayload[1];
        if (sub != StartRoutine && sub != StopRoutine && sub != RequestRoutineResults)
        {
            // §11.7.4.1: undefined sub-function -> NRC $12.
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // Defence-in-depth: the persona swap to UdsKernelPersona happens after
        // $36 sub $80 DownloadAndExecute, which in turn presupposes a $34
        // RequestDownload, which presupposes $27 unlock. So a kernel call
        // arriving here while locked is impossible in normal flows. Still
        // enforce explicitly so a test/UI that pokes node.Persona directly
        // can't accidentally bypass the unlock.
        if (node.State.SecurityUnlockedLevel == 0)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SecurityAccessDenied);
            return false;
        }

        byte idHi = usdtPayload[2];
        byte idLo = usdtPayload[3];
        ushort routineId = (ushort)((idHi << 8) | idLo);

        // Capture-mode side effect: record erase region so $36 writes mirror
        // into a consolidated flash buffer dumped at session end. The kernel's
        // $FF00 EraseMemoryByAddress carries an 8-byte option record (BE uint32
        // start + BE uint32 size). Other routines (e.g. $0401 CheckMemory) and
        // other sub-functions are ignored here.
        if (sub == StartRoutine
            && routineId == RoutineIdEraseMemoryByAddress
            && usdtPayload.Length == 12
            && ch.Bus?.Capture.BootloaderCaptureEnabled == true)
        {
            uint start = ((uint)usdtPayload[4] << 24)
                       | ((uint)usdtPayload[5] << 16)
                       | ((uint)usdtPayload[6] << 8)
                       |  (uint)usdtPayload[7];
            uint size  = ((uint)usdtPayload[8] << 24)
                       | ((uint)usdtPayload[9] << 16)
                       | ((uint)usdtPayload[10] << 8)
                       |  (uint)usdtPayload[11];

            if (size > 0 && size <= MaxFlashEraseRegionBytes
                && (long)start + size <= 0x1_0000_0000L)
            {
                node.State.CapturedFlashRegions.Add(new Ecu.FlashEraseRegion(start, size));
                ch.Bus?.LogDiagnostic?.Invoke(
                    $"[$31 erase] region recorded: 0x{start:X8} +{size} ({size / 1024} KiB)");
            }
            else
            {
                ch.Bus?.LogDiagnostic?.Invoke(
                    $"[$31 erase] rejected region 0x{start:X8} +{size}: size out of range");
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
                return false;
            }
        }

        // statusRecord = $00 (routine completed successfully). Real ECUs vary
        // per routine; this is the common "ok" shape that GM SPS tools accept.
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(sid), sub, idHi, idLo, 0x00]);
        return true;
    }
}
