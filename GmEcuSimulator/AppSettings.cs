using System.IO;
using System.Text.Json;

namespace GmEcuSimulator;

// Per-user UI preferences. Lives at %LOCALAPPDATA%\GmEcuSimulator\settings.json
// (next to the bus_*.csv files written by FileLogSink). Separate from the
// per-config SimulatorConfig because these are app-wide UI choices, not
// part of any one ECU profile - switching configs shouldn't flip the log
// menu's checkboxes.
//
// All fields default to a sensible "off" or "include" state so a missing /
// corrupt settings.json still produces a usable simulator.
public sealed class AppSettings
{
    /// <summary>Master gate: when on, FileLogSink streams to a fresh bus_*.csv.</summary>
    public bool LogToFile { get; set; }

    /// <summary>When on (and LogToFile is on), AppendLog J2534 calls reach the file.</summary>
    public bool LogIncludeJ2534Calls { get; set; } = true;

    /// <summary>When on (and LogToFile is on), AppendBusFrame CAN frames reach the file.</summary>
    public bool LogIncludeBusTraffic { get; set; } = true;

    /// <summary>When on, every bus-frame log line is suffixed with a Gmw3110Annotator tag.</summary>
    public bool LogAppendDescriptionTag { get; set; }

    /// <summary>
    /// When on, long ISO-TP transfers in the bus log are condensed: the
    /// middle of each transfer is replaced with a "... N frames hidden ..."
    /// marker, with the first/last few CFs kept on either side.
    /// </summary>
    public bool LogCollapseBulkTransfers { get; set; }

    public static string DefaultPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "GmEcuSimulator", "settings.json");
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
