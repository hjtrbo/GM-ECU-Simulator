namespace Core.Bus;

// Runtime toggle for bootloader-capture mode. When BootloaderCaptureEnabled is
// false (the default), Service36Handler enforces the spec-correct GMW3110
// §8.13.4 NRC $31 bounds check and no capture-to-disk happens. When set true
// (via the Capture Bootloader tab), Service36Handler relaxes the bounds check
// so a real host's RAM-resident SPS bootloader payload at addresses outside
// the $34-declared range still lands in the sink buffer, and EcuExitLogic
// writes the assembled buffer to disk when the programming session ends.
//
// Single global flag - intentionally not per-ECU. The user's workflow is
// "switch on, run one tool against one ECU, switch off"; per-ECU plumbing
// would just add UI noise for a one-knob feature.
//
// Capture writes go to %LOCALAPPDATA%\GmEcuSimulator\captures unless overridden.
public sealed class CaptureSettings
{
    public bool BootloaderCaptureEnabled { get; set; }

    /// <summary>
    /// Directory the EcuExitLogic capture writer drops .bin files into when
    /// BootloaderCaptureEnabled is true and a session ends with bytes received.
    /// Default points at %LOCALAPPDATA%\GmEcuSimulator\captures and is created
    /// on first write; callers can swap to a test temp dir.
    /// </summary>
    public string CaptureDirectory { get; set; } = DefaultDirectory();

    /// <summary>
    /// Raised after a capture file is successfully written. Argument is the
    /// full path to the written .bin. UI subscribes to refresh the captured-
    /// downloads list without polling.
    /// </summary>
    public event Action<string>? CaptureWritten;

    internal void RaiseCaptureWritten(string path) => CaptureWritten?.Invoke(path);

    private static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(local, "GmEcuSimulator", "captures");
    }
}
