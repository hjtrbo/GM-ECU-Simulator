using Common;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmEcuSimulator;

// Per-user UI preferences. Lives at %LOCALAPPDATA%\GmEcuSimulator\config\settings.json
// (next to the bus_*.csv files written by FileLogSink). Separate from the
// per-config SimulatorConfig because these are app-wide UI choices, not
// part of any one ECU profile - switching configs shouldn't flip the log
// menu's checkboxes.
//
// All fields default to a sensible "off" or "include" state so a missing /
// corrupt settings.json still produces a usable simulator.
public sealed class AppSettings
{
    /// <summary>Master gate: when on, BusLogger streams to a fresh bus_*.csv.</summary>
    public bool LogToFile { get; set; }

    /// <summary>When on (and LogToFile is on), [J2534] and [SIM] tagged lines reach the file.</summary>
    public bool LogIncludeJ2534Calls { get; set; } = true;

    /// <summary>When on (and LogToFile is on), [CAN] frame lines reach the file.</summary>
    public bool LogIncludeBusTraffic { get; set; } = true;

    /// <summary>When on, every bus-frame log line is suffixed with a Gmw3110Annotator tag.</summary>
    public bool LogAppendDescriptionTag { get; set; }

    /// <summary>
    /// When on, long ISO-TP transfers in the bus log are condensed: the
    /// middle of each transfer is replaced with a "... N frames hidden ..."
    /// marker, with the first/last few CFs kept on either side.
    /// </summary>
    public bool LogCollapseBulkTransfers { get; set; }

    /// <summary>
    /// When on, the simulator drives $3E TesterPresent on the host's behalf
    /// for any frame registered via PassThruStartPeriodicMsg. When off the
    /// registration succeeds but no timer ticks - delegating hosts must keep
    /// their own P3C session alive. Default true matches GM Tech 2 / GDS2
    /// behaviour, which is what most users expect.
    /// </summary>
    public bool AllowPeriodicTesterPresent { get; set; } = true;

    /// <summary>
    /// UI-only filter: when on, $3E TesterPresent requests and $7E positive
    /// responses are hidden from the bus log textbox (and the Download tab
    /// mirror). The file-log capture is unaffected - this is purely a
    /// readability tweak for the live window.
    /// </summary>
    public bool LogSuppressTesterPresentInWindow { get; set; }

    /// <summary>
    /// Active top-level mode (ECU Simulator / DPS Simulator).
    /// Drives which per-mode config file the app loads and saves, single-ECU
    /// vs multi-ECU constraints, and which tabs / fields are visible. Default
    /// is <see cref="AppMode.EcuSimulator"/> so fresh installs land in the
    /// original multi-ECU behaviour.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppMode Mode { get; set; } = AppMode.EcuSimulator;

    /// <summary>
    /// Active connection type (transport) for the current mode: the J2534
    /// named-pipe shim, or the localhost raw-CAN TCP listener that a gauge
    /// simulator connects to. Selected as a sub-variant inside the mode
    /// dropdown; exactly one transport is live at a time. Default
    /// <see cref="ConnectionType.J2534"/> keeps fresh installs on the original
    /// registry-discovered shim path.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConnectionType ConnectionType { get; set; } = ConnectionType.J2534;

    /// <summary>
    /// Last directory the user picked an archive from in the DPS prime wizard.
    /// Used as InitialDirectory for the next archive pick so the wizard opens
    /// where you were last working instead of wherever Windows' MRU points.
    /// Null on a fresh install or after a corrupt settings.json.
    /// </summary>
    public string? PrimeWizardArchiveDir { get; set; }

    /// <summary>
    /// Last directory the user picked an .bin file from for the prime
    /// wizard's "Load from bin..." button on the Phase 3 review page.
    /// Tracked separately from <see cref="PrimeWizardArchiveDir"/> because
    /// archives and donor / sibling bins typically live in different folders.
    /// </summary>
    public string? PrimeWizardBinLoadDir { get; set; }

    /// <summary>
    /// Last directory used by File > Open / Save As of the full simulator
    /// config (*.json). Shared across both dialogs - they all read and write
    /// the same on-disk schema, so users typically keep them in one folder.
    /// </summary>
    public string? LastConfigDir { get; set; }

    /// <summary>
    /// Last directory used by Setup > Save PIDs / Load PIDs (*.pids.json).
    /// Separate from <see cref="LastConfigDir"/> because PID-only exports are
    /// often shared between ECUs and may live in a different working folder
    /// from the full configs.
    /// </summary>
    public string? LastPidListDir { get; set; }

    /// <summary>
    /// Last directory used by the ECU editor's "Load info from bin..."
    /// command. Separate from <see cref="PrimeWizardBinLoadDir"/> because
    /// the editor's flow targets a single ECU (DID enrichment from a flash
    /// image) and the wizard's flow targets a full prime - users may keep
    /// donor bins and full readbacks in different folders.
    /// </summary>
    public string? LastBinDir { get; set; }

    /// <summary>
    /// Last directory used by Bin Replay > Load file. Replay bins are
    /// usually scenario / capture files, often kept apart from flash bins
    /// and DPS archives, so this gets its own slot.
    /// </summary>
    public string? LastBinReplayDir { get; set; }

    /// <summary>
    /// Last directory used by the per-PID CSV waveform picker. Separate from
    /// <see cref="LastBinReplayDir"/> because CSV traces are often
    /// hand-crafted or exported from scope tools and live in a different
    /// folder than full bin captures.
    /// </summary>
    public string? LastCsvWaveformDir { get; set; }

    /// <summary>
    /// Helper for dialog InitialDirectory seeding: returns <paramref name="dir"/>
    /// when non-null/empty and still present on disk, else empty string (which
    /// makes Microsoft.Win32 dialogs fall back to the Windows MRU). Used by
    /// the File and ECU editor pickers to round-trip last-used dirs.
    /// </summary>
    public static string ResolveInitialDir(string? dir)
        => (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ? dir : string.Empty;

    public static string DefaultPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "GmEcuSimulator", "config", "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = DefaultPath;
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt file or permission issue - silently fall back to defaults
            // so the app always starts. The user can fix settings.json by hand
            // or just let the next Save() overwrite it.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = DefaultPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Save failures are non-fatal - preferences just won't persist
            // across this session.
        }
    }
}
