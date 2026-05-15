using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Ecu.Personas;

namespace Core.Services;

// $36 TransferData. GMW3110-2010 §8.13 (p161-167).
//
// Request: SID $36 + sub-function ($00 Download or $80 DownloadAndExecute)
//          + startingAddress (2..4 bytes BE) + dataRecord (variable).
// Positive response: $76 (no data parameters).
//
// NRCs (§8.13.4, Table 142):
//   $12 SFNS-IF   - sub-function invalid, or length too short for sub-function
//   $22 CNCRSE    - TransferData_Allowed = NO (i.e., $34 not run first)
//   $31 ROOR      - startingAddress invalid, or start + length exceeds buffer
//   $78 RCR-RP    - cannot process within P2C (we never need this)
//   $83 VOLTRNG   - voltage out of range (we never need this)
//   $85 PROGFAIL  - program/erase/CRC failure (we never need this)
//
// The startingAddress byte-count is fixed per ECU and must match across all
// $36 calls in the same session (§8.13.2 Note 1). We use the value frozen by
// the most recent $34 in NodeState.DownloadAddressByteCount.
//
// Spec mode (default, bus.Capture.BootloaderCaptureEnabled = false):
//   startingAddress is interpreted as an offset into the $34-declared buffer.
//   Out-of-range writes get NRC $31. Behaviour matches §8.13.4 exactly.
//
// Capture mode (bus.Capture.BootloaderCaptureEnabled = true):
//   Real GM hosts send the absolute RAM/flash address (e.g. $003FB800) where
//   the SPS bootloader expects to land - that's wildly outside the small
//   declared buffer and would always trip NRC $31 in spec mode. In capture
//   mode we treat the FIRST $36's startingAddress as the base, store the
//   payload at (addr - base), and grow the sink buffer past the declared
//   size as needed (capped at 64 MiB to bound runaway allocations). The
//   captured buffer is written to disk by EcuExitLogic when the session ends.
public static class Service36Handler
{
    /// <summary>Safety cap on the capture-mode sink buffer. 64 MiB is well past
    /// any real GM SPS bootloader/calibration payload (~10s of KB to a few MB)
    /// and prevents a malformed host address from triggering an OOM allocation.</summary>
    public const int MaxCaptureBufferBytes = 64 * 1024 * 1024;

    /// <summary>Returns true if a positive response was sent, false if an NRC was sent.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        int addrBytes = node.State.DownloadAddressByteCount;
        // Minimum length: 1 SID + 1 sub + addrBytes + 1 data byte (sub $00 requires data).
        // For sub $80, data may be empty.
        if (usdtPayload.Length < 2 + addrBytes || usdtPayload[0] != Service.TransferData)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte sub = usdtPayload[1];
        if (sub != 0x00 && sub != 0x80)
        {
            // §8.13.2.1: only $00 (Download) and $80 (DownloadAndExecute) are defined.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // §8.13.4: $00 requires at least one data byte.
        if (sub == 0x00 && usdtPayload.Length == 2 + addrBytes)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        if (!node.State.DownloadActive || node.State.DownloadBuffer is null)
        {
            // §8.13.4 NRC $22: $34 not run / TransferData_Allowed = NO.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // Decode startingAddress as a BE unsigned integer.
        uint startingAddress = 0;
        for (int i = 0; i < addrBytes; i++)
            startingAddress = (startingAddress << 8) | usdtPayload[2 + i];

        var dataRecord = usdtPayload.Slice(2 + addrBytes);

        bool captureOn = ch.Bus?.Capture.BootloaderCaptureEnabled == true;
        if (captureOn)
            return HandleCapture(node, ch, sub, startingAddress, dataRecord);

        // -------- Spec mode (default) --------
        // §8.13.4 NRC $31: out-of-range. startingAddress is treated as an offset
        // into the declared buffer; anything past the end is rejected.
        long endExclusive = (long)startingAddress + dataRecord.Length;
        if (endExclusive > node.State.DownloadBuffer.Length)
        {
            // Diagnostic breadcrumb: when a real host sends an absolute RAM/flash
            // address (typical for SPS bootloader uploads), this NRC fires every
            // single time in spec mode. The user almost certainly wants capture
            // mode in that scenario - surface the hint in the bus log so they
            // don't have to step into the handler to figure out why.
            ch.Bus?.LogDiagnostic?.Invoke(
                $"[$36 NRC $31] startingAddress=0x{startingAddress:X8} + dataLen={dataRecord.Length} exceeds declared buffer ({node.State.DownloadBuffer.Length}). " +
                "Tick 'Enable bootloader capture' in the Capture Bootloader tab to relax this check and dump the payload to disk.");
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange);
            return false;
        }

        if (dataRecord.Length > 0)
        {
            dataRecord.CopyTo(node.State.DownloadBuffer.AsSpan((int)startingAddress));
            node.State.DownloadBytesReceived += (uint)dataRecord.Length;
        }

        // Sub $80 DownloadAndExecute hands the bus to the just-uploaded SPS
        // kernel. Swap the ECU's persona so subsequent $31/etc. requests are
        // dispatched by UdsKernelPersona; EcuExitLogic resets it on $20 / P3C
        // timeout. Done before the positive response so the wire ordering
        // matches a real kernel handover.
        if (sub == 0x80)
            node.Persona = UdsKernelPersona.Instance;

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.TransferData)]);
        return true;
    }

    /// <summary>
    /// Capture-mode path: lock the base address on the first $36, store payload
    /// relative to that base, grow the buffer (capped) when the host writes
    /// past the declared size. Never returns NRC $31.
    /// </summary>
    private static bool HandleCapture(EcuNode node, ChannelSession ch, byte sub, uint startingAddress,
                                      ReadOnlySpan<byte> dataRecord)
    {
        node.State.DownloadCaptureBaseAddress ??= startingAddress;
        uint baseAddr = node.State.DownloadCaptureBaseAddress.Value;

        if (startingAddress < baseAddr)
        {
            // Host wrote BEFORE the address it first used. Rebase: shift the
            // existing buffer forward so the new low address becomes offset 0.
            uint shift = baseAddr - startingAddress;
            int oldLen = node.State.DownloadBuffer!.Length;
            long needed = (long)oldLen + shift;
            if (needed > MaxCaptureBufferBytes)
            {
                ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange);
                return false;
            }
            var rebased = new byte[needed];
            Buffer.BlockCopy(node.State.DownloadBuffer, 0, rebased, (int)shift, oldLen);
            node.State.DownloadBuffer = rebased;
            node.State.DownloadCaptureBaseAddress = startingAddress;
            // Existing data has moved up by `shift` bytes in the buffer; the
            // high-water mark tracks the highest written offset and must shift
            // too so the trim at flush still covers all real writes.
            node.State.DownloadCaptureHighWaterMark += shift;
            baseAddr = startingAddress;
        }

        long offset = startingAddress - baseAddr;
        long endExclusive = offset + dataRecord.Length;
        if (endExclusive > MaxCaptureBufferBytes)
        {
            // Hard safety cap - reject and let the host see something rather
            // than allocating multi-GB. $31 is the closest match.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange);
            return false;
        }

        if (endExclusive > node.State.DownloadBuffer!.Length)
        {
            // Grow with some headroom so a contiguous download doesn't
            // realloc on every $36. Cap at MaxCaptureBufferBytes.
            long target = Math.Min(MaxCaptureBufferBytes, Math.Max(endExclusive, node.State.DownloadBuffer.Length * 2L));
            var grown = new byte[target];
            Buffer.BlockCopy(node.State.DownloadBuffer, 0, grown, 0, node.State.DownloadBuffer.Length);
            node.State.DownloadBuffer = grown;
        }

        if (dataRecord.Length > 0)
        {
            dataRecord.CopyTo(node.State.DownloadBuffer.AsSpan((int)offset));
            node.State.DownloadBytesReceived += (uint)dataRecord.Length;
            if ((uint)endExclusive > node.State.DownloadCaptureHighWaterMark)
                node.State.DownloadCaptureHighWaterMark = (uint)endExclusive;

            // Per-$36 immediate write: dump this transfer's raw data record
            // to its own .bin file. The buffer-reassembly above is kept for
            // unit-test introspection and for any future "sparse memory
            // image" sidecar; the per-$36 .bin is what the user actually
            // reads.
            BootloaderCaptureWriter.WriteEachTransferData(node, ch.Bus!, startingAddress, dataRecord);

            // Flash-region mirror: if a $31 EraseMemoryByAddress earlier this
            // session declared a region that fully contains this $36's
            // [startingAddress, endAddress) range, copy the dataRecord into
            // the region's $FF-backed buffer. At session end EcuExitLogic
            // dumps one .bin per region so the captures dir holds the
            // consolidated calibration / OS image the kernel actually wrote.
            // Partial overlaps with a region (start inside / end outside, or
            // vice versa) are ignored - the kernel only sends in-region
            // writes after declaring the erase; an out-of-region write is a
            // kernel bug we don't want to silently truncate.
            long endAddrExclusive = (long)startingAddress + dataRecord.Length;
            foreach (var region in node.State.CapturedFlashRegions)
            {
                if (startingAddress >= region.StartAddress
                    && endAddrExclusive <= (long)region.StartAddress + region.Size)
                {
                    int regionOffset = (int)(startingAddress - region.StartAddress);
                    dataRecord.CopyTo(region.Buffer.AsSpan(regionOffset));
                    region.BytesWritten += (uint)dataRecord.Length;
                }
            }
        }

        // Sub $80 DownloadAndExecute in capture mode behaves like spec mode
        // for persona purposes: the kernel "took control", so subsequent
        // requests dispatch through UdsKernelPersona until $20 / P3C resets.
        if (sub == 0x80)
            node.Persona = UdsKernelPersona.Instance;

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.TransferData)]);
        return true;
    }
}
