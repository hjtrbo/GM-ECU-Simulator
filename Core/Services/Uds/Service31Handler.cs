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
//
// Response shapes are NOT spec-uniform on real GM kernels:
//   $FF00 Erase returns the ISO 14229 §11.7.2 layout
//       [$71][$01][$FF][$00][$10]
//     i.e. sub-function echo + routineId echo + 1-byte status.
//   $0401 CheckMemory returns a kernel-specific layout
//       [$71][$04][crc_hi][crc_lo]
//     - no sub-function echo, no routineId echo. The $04 is a fixed opcode
//     the kernel uses to mark "CheckMemory result"; the trailing 2 bytes are
//     a CRC-16/CCITT-FALSE over the bytes the tester wrote via $36 in the
//     requested [startAddress, startAddress+size) range. Verified against
//     powerpcm_flasher 0.0.0.6 (Hauptfenster.cs:1697-1750): if the wire
//     reply doesn't match $71 $04 ... the tester loops up to 30 times then
//     fails the flash; the spec-shape $71 $01 $04 $01 $00 freezes its UI.
//
// All other startRoutine/stopRoutine/requestRoutineResults paths keep the
// spec-shape positive response with statusRecord = $00 (completed) - that's
// what real kernels send for routines the simulator doesn't model deeply.
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

    /// <summary>Routine ID for "Check Memory by Address" used by GM SPS kernels.
    /// The kernel response shape for this routine is NOT spec-uniform - see
    /// the header comment for details.</summary>
    public const ushort RoutineIdCheckMemoryByAddress = 0x0401;

    /// <summary>Fixed opcode byte the kernel emits in its CheckMemoryByAddress
    /// response, between the positive-response SID and the 2-byte CRC. Verified
    /// from powerpcm_flasher's response parser at Hauptfenster.cs:1736-1747.</summary>
    public const byte CheckMemoryResponseOpcode = 0x04;

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

        // $0401 CheckMemoryByAddress uses a kernel-specific response shape -
        // [$71][$04][crc_hi][crc_lo] - not the spec-shape sub/id echo. See
        // header comment. The CRC is computed over the bytes the tester wrote
        // via $36 inside the requested range; if no captured flash region
        // covers that range we fall back to CRC=$0000 so the tester prints
        // "NOT valid!" and exits its loop instead of waiting 30 polls.
        if (sub == StartRoutine
            && routineId == RoutineIdCheckMemoryByAddress
            && usdtPayload.Length == 12)
        {
            uint start = ((uint)usdtPayload[4] << 24)
                       | ((uint)usdtPayload[5] << 16)
                       | ((uint)usdtPayload[6] << 8)
                       |  (uint)usdtPayload[7];
            uint size  = ((uint)usdtPayload[8] << 24)
                       | ((uint)usdtPayload[9] << 16)
                       | ((uint)usdtPayload[10] << 8)
                       |  (uint)usdtPayload[11];

            ushort crc = ComputeCheckMemoryCrc(node, start, size, ch);
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                [Service.Positive(sid), CheckMemoryResponseOpcode,
                 (byte)(crc >> 8), (byte)(crc & 0xFF)]);
            return true;
        }

        // statusRecord = $00 (routine completed successfully). Real ECUs vary
        // per routine; this is the common "ok" shape that GM SPS tools accept.
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(sid), sub, idHi, idLo, 0x00]);
        return true;
    }

    // Locates the captured flash region that fully contains [start, start+size)
    // and runs CRC-16/CCITT-FALSE over the matching slice of its mirror buffer.
    // The mirror is populated by Service36Handler when capture mode is on;
    // partial overlaps are not enough because the kernel's CRC is defined over
    // a contiguous range and a partial mirror would silently fabricate $FF
    // bytes for the uncovered tail. Falls back to $0000 (which the tester
    // treats as a non-match) when no region qualifies.
    private static ushort ComputeCheckMemoryCrc(EcuNode node, uint start, uint size,
                                                ChannelSession ch)
    {
        foreach (var region in node.State.CapturedFlashRegions)
        {
            long endExclusive = (long)start + size;
            if (start >= region.StartAddress
                && endExclusive <= (long)region.StartAddress + region.Size
                && size <= int.MaxValue)
            {
                int offset = (int)(start - region.StartAddress);
                ushort crc = Crc16Ccitt.Compute(region.Buffer.AsSpan(offset, (int)size));
                ch.Bus?.LogDiagnostic?.Invoke(
                    $"[$31 check] CRC ${crc:X4} over 0x{start:X8} +{size} " +
                    $"(region 0x{region.StartAddress:X8} +{region.Size}, " +
                    $"{region.BytesWritten} bytes written)");
                return crc;
            }
        }

        ch.Bus?.LogDiagnostic?.Invoke(
            $"[$31 check] CRC fallback to $0000: no captured flash region covers " +
            $"0x{start:X8} +{size}. Enable 'Capture bootloader' so $31 $FF00 erase " +
            "records the region and $36 mirrors writes into it.");
        return 0x0000;
    }
}
