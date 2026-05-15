using System.Globalization;
using System.IO;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// Writes the assembled $36 sink buffer to disk when capture mode is on and a
// programming session ends with bytes received. Called from EcuExitLogic
// before ClearProgrammingState() wipes the buffer, AND from Service34Handler
// when a subsequent $34 rotates the buffer to a new logical kernel bin.
//
// File layout:
//   {captureDir}/{ecuName}_{sessionUtc:yyyyMMdd_HHmmss}_{seq:D2}_{baseAddr:X8}_{bytes}.bin
//
// One file per $34-bracketed transfer. All files from the same session share
// the same UTC timestamp (pinned on the first $34) so they group visually in
// the directory listing. The seq counter disambiguates same-session files.
public static class BootloaderCaptureWriter
{
    /// <summary>
    /// If capture mode is on and the node received any $36 bytes since the
    /// most recent $34, writes the buffer (trimmed to the high-water mark)
    /// to disk and raises the bus's CaptureWritten event. No-op in every
    /// other case. Swallows IO errors after logging via VirtualBus.LogDiagnostic
    /// - a failed capture write must not break the rest of the caller's flow.
    /// </summary>
    public static void MaybeWrite(EcuNode node, VirtualBus bus)
    {
        var settings = bus.Capture;
        if (!settings.BootloaderCaptureEnabled) return;
        if (node.State.DownloadBytesReceived == 0) return;
        if (node.State.DownloadBuffer is null) return;

        try
        {
            Directory.CreateDirectory(settings.CaptureDirectory);

            uint baseAddr = node.State.DownloadCaptureBaseAddress ?? 0;
            uint highWater = node.State.DownloadCaptureHighWaterMark;
            // Defensive: if high-water somehow wasn't tracked but bytes were
            // received, fall back to the full buffer length so we don't drop
            // data on the floor.
            int writeLen = highWater > 0
                ? (int)Math.Min(highWater, (uint)node.State.DownloadBuffer.Length)
                : node.State.DownloadBuffer.Length;
            if (writeLen == 0) return;

            DateTime tsUtc = node.State.DownloadCaptureSessionTimestampUtc ?? DateTime.UtcNow;
            uint seq = node.State.DownloadCaptureSequence;
            string ts = tsUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}_{2:D2}_{3:X8}_{4}.bin",
                Sanitise(node.Name), ts, seq, baseAddr, writeLen);
            string path = Path.Combine(settings.CaptureDirectory, fileName);

            File.WriteAllBytes(path, node.State.DownloadBuffer.AsSpan(0, writeLen).ToArray());

            bus.LogDiagnostic?.Invoke($"[capture] wrote {writeLen} bytes (seq {seq}, base 0x{baseAddr:X8}) -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogDiagnostic?.Invoke($"[capture] write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Per-$36 immediate write. Each $36 TransferData's dataRecord is dumped
    /// to its own .bin file - no reassembly buffer, no sparse-image offset
    /// math, no $34-bracket merging. Filename embeds a session-scoped seq
    /// (D3 since a programming session can issue dozens of $36s), the $36's
    /// startingAddress, and the dataRecord length.
    ///
    /// Result: a flash-tool author scanning the captures dir sees one file
    /// per logical "push" - the kernel pieces are obvious by their distinct
    /// sizes/addresses, staging-buffer cal chunks are obvious by repetition.
    /// </summary>
    public static void WriteEachTransferData(EcuNode node, VirtualBus bus,
                                             uint startingAddress, ReadOnlySpan<byte> dataRecord)
    {
        var settings = bus.Capture;
        if (!settings.BootloaderCaptureEnabled) return;
        if (dataRecord.Length == 0) return;

        try
        {
            // Pin the session timestamp on the first capture write so every
            // .bin from this session shares a stable yyyymmdd_HHmmss prefix.
            if (node.State.DownloadCaptureSessionTimestampUtc is null)
                node.State.DownloadCaptureSessionTimestampUtc = DateTime.UtcNow;

            var tsUtc = node.State.DownloadCaptureSessionTimestampUtc.Value;
            uint seq = node.State.DownloadCaptureSequence;
            string ts = tsUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            // Per-session subdirectory so a fresh download doesn't fill the
            // captures root with dozens of loose files. Subdir name pins the
            // ECU + session start; filename inside is just seq + addr + len.
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory, sessionDir);
            Directory.CreateDirectory(fullDir);

            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0:D3}_{1:X8}_{2}.bin", seq, startingAddress, dataRecord.Length);
            string path = Path.Combine(fullDir, fileName);

            File.WriteAllBytes(path, dataRecord.ToArray());
            node.State.DownloadCaptureSequence++;

            bus.LogDiagnostic?.Invoke($"[capture] $36 #{seq}: {dataRecord.Length} B @ 0x{startingAddress:X8} -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogDiagnostic?.Invoke($"[capture] write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Session-end consolidated flash dump. For every region the kernel
    /// declared via $31 EraseMemoryByAddress, writes one .bin sized to the
    /// declared erase length (any byte not overwritten by a $36 stays at
    /// the post-erase $FF, matching the on-device reality). Files land in
    /// the same per-session subdirectory as the per-$36 fragments so a
    /// user can compare the contiguous image against the individual pieces.
    ///
    /// Naming: {seq:D3}_flash_{start:X8}_{size}.bin. The leading seq comes
    /// from DownloadCaptureSequence so the flash dump sorts after the
    /// individual $36 fragments that built it.
    ///
    /// Called from EcuExitLogic before ClearProgrammingState wipes the
    /// regions. No-op when capture mode is off or no region was declared.
    /// </summary>
    public static void WriteFlashRegions(EcuNode node, VirtualBus bus)
    {
        var settings = bus.Capture;
        if (!settings.BootloaderCaptureEnabled) return;
        if (node.State.CapturedFlashRegions.Count == 0) return;

        try
        {
            var tsUtc = node.State.DownloadCaptureSessionTimestampUtc ?? DateTime.UtcNow;
            string ts = tsUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory, sessionDir);
            Directory.CreateDirectory(fullDir);

            foreach (var region in node.State.CapturedFlashRegions)
            {
                uint seq = node.State.DownloadCaptureSequence++;
                string fileName = string.Format(CultureInfo.InvariantCulture,
                    "{0:D3}_flash_{1:X8}_{2}.bin", seq, region.StartAddress, region.Size);
                string path = Path.Combine(fullDir, fileName);

                File.WriteAllBytes(path, region.Buffer);

                bus.LogDiagnostic?.Invoke(
                    $"[capture] flash region 0x{region.StartAddress:X8} +{region.Size} " +
                    $"({region.BytesWritten} B written, rest 0xFF) -> {path}");
                settings.RaiseCaptureWritten(path);
            }
        }
        catch (Exception ex)
        {
            bus.LogDiagnostic?.Invoke($"[capture] flash region write failed: {ex.Message}");
        }
    }

    private static string Sanitise(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            buf[i] = Array.IndexOf(invalid, raw[i]) >= 0 ? '_' : raw[i];
        return new string(buf);
    }
}
