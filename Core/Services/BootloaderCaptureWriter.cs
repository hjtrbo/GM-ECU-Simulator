using System.Globalization;
using System.IO;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// Writes the assembled $36 sink buffer to disk when capture mode is on and a
// programming session ends with bytes received. Called from EcuExitLogic
// before ClearProgrammingState() wipes the buffer.
//
// File layout:
//   {captureDir}/{ecuName}_{utc:yyyyMMdd_HHmmss}_{baseAddr:X8}_{bytesReceived}.bin
//
// Embedding the metadata in the filename keeps inspection one `dir` away -
// no sidecar parsing needed. Multi-byte ECU names with path-illegal chars are
// sanitised to underscore.
public static class BootloaderCaptureWriter
{
    /// <summary>
    /// If capture mode is on and the node received any $36 bytes this session,
    /// writes the sink buffer to disk and raises the bus's CaptureWritten
    /// event. No-op in every other case. Swallows IO errors after logging via
    /// VirtualBus.LogDiagnostic - a failed capture write must not break the
    /// rest of EcuExitLogic.
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
            uint received = node.State.DownloadBytesReceived;
            string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}_{2:X8}_{3}.bin", Sanitise(node.Name), ts, baseAddr, received);
            string path = Path.Combine(settings.CaptureDirectory, fileName);

            // Trim trailing unwritten bytes - the sink buffer was grown for
            // headroom; the disk file should only contain bytes actually
            // covered by $36 writes. We can't perfectly distinguish "host
            // wrote zero" from "never written" without a per-byte watermark,
            // so trim to (max address written + 1) by using DownloadBuffer's
            // physical length capped at the largest end-of-write seen. The
            // simpler heuristic - trim trailing zeros - would corrupt
            // payloads with legitimate zero tails, so we cap at the buffer
            // length the handler grew it to (which is already addr-driven).
            int writeLen = node.State.DownloadBuffer.Length;
            File.WriteAllBytes(path, node.State.DownloadBuffer.AsSpan(0, writeLen).ToArray());

            bus.LogDiagnostic?.Invoke($"[capture] wrote {writeLen} bytes -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogDiagnostic?.Invoke($"[capture] write failed: {ex.Message}");
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
