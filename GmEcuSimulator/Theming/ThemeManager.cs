using System.Collections;
using System.IO;
using System.Windows;

namespace GmEcuSimulator.Theming;

// Runtime palette switcher.
//
// Each palette under /Resources/Theme/Palettes/<Name>.xaml is a complete
// self-contained set of brush + color resources. ThemeManager installs one
// at startup as a top-level entry in Application.Resources.MergedDictionaries
// and swaps it out on Apply().
//
// Why dict-swap (not in-place mutation)? BAML-loaded brushes come back frozen
// from Application.LoadComponent - SolidColorBrush.Color throws if mutated.
// Swapping the dictionary works as long as every brush reference uses
// {DynamicResource ...} rather than {StaticResource ...} - DynamicResource
// re-resolves through the merged-dictionary chain on every property access,
// so every templated control sees the new brushes immediately.
//
// "Plug-in" model: built-in palettes ship as embedded XAML resources. User
// palettes can be dropped into %APPDATA%\GmEcuSimulator\Palettes\ as loose
// .xaml files and they appear in AvailablePalettes alongside the built-ins.
public static class ThemeManager
{
    public sealed record PaletteEntry(string Name, string DisplayName, string? FilePath, bool IsUser);

    private const string DefaultPaletteName = "Midnight";

    private static readonly string[] BuiltIns =
    {
        // Dark
        "Midnight",
        "Graphite",
        "Solarized",
        "Nord",
        "Dracula",
        "Tokyo",
        // Mid
        "Slate",
        "Mauve",
        // Light
        "Daylight",
        "Frost",
        "Lavender",
    };

    public static IReadOnlyList<PaletteEntry> AvailablePalettes { get; private set; } = Array.Empty<PaletteEntry>();
    public static string ActivePalette { get; private set; } = DefaultPaletteName;
    public static event Action<string>? PaletteChanged;

    /// <summary>
    /// %APPDATA%\GmEcuSimulator\Palettes\ - where the user's own .xaml
    /// palette files live. Created on first access so the user has a place
    /// to drop files even before they look for the folder.
    /// </summary>
    public static string UserPaletteDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmEcuSimulator",
                "Palettes");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>
    /// Scans the built-in registry and the user palette directory to compose
    /// AvailablePalettes. Cheap; call on startup and whenever the user adds
    /// a palette file.
    /// </summary>
    public static void RefreshAvailable()
    {
        var list = new List<PaletteEntry>();
        foreach (var name in BuiltIns)
            list.Add(new PaletteEntry(name, name, null, IsUser: false));

        try
        {
            foreach (var path in Directory.EnumerateFiles(UserPaletteDirectory, "*.xaml"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (list.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new PaletteEntry(name, name, path, IsUser: true));
            }
        }
        catch
        {
            // Directory enumeration can fail (perms, missing drive); built-ins
            // still surface so the menu never goes empty.
        }

        AvailablePalettes = list;
    }

    /// <summary>
    /// Called once during App.OnStartup. Copies the named palette's keys
    /// directly into Application.Resources (the local dictionary, not a
    /// merged sub-dictionary). Resource lookup checks local keys before
    /// MergedDictionaries, so these always win, and per-key Set fires the
    /// ResourcesChanged notifications DynamicResource subscribers actually
    /// listen to.
    /// </summary>
    public static void InstallInitialPalette(string name = DefaultPaletteName)
    {
        var app = Application.Current;
        if (app == null) return;

        var dict = LoadPaletteDictionary(name);
        if (dict == null)
        {
            DiagLog($"InstallInitialPalette('{name}'): load failed");
            return;
        }

        CopyKeysInto(dict, app.Resources);
        ActivePalette = name;
        DiagLog($"InstallInitialPalette('{name}'): copied {dict.Count} keys into Application.Resources");
    }

    /// <summary>
    /// Switches the live UI to the named palette. Replaces every brush /
    /// color key directly in Application.Resources - each Set is a discrete
    /// resource invalidation that DynamicResource consumers react to.
    /// </summary>
    public static bool Apply(string name)
    {
        var app = Application.Current;
        if (app == null) return false;

        var entry = AvailablePalettes.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        var newDict = LoadPaletteDictionary(name);
        if (newDict == null) return false;

        var beforeBrush = app.Resources["Bg.SurfaceBrush"] as System.Windows.Media.SolidColorBrush;
        var beforeColor = beforeBrush?.Color.ToString() ?? "<null>";

        CopyKeysInto(newDict, app.Resources);

        var afterBrush = app.Resources["Bg.SurfaceBrush"] as System.Windows.Media.SolidColorBrush;
        var afterColor = afterBrush?.Color.ToString() ?? "<null>";

        ActivePalette = entry?.Name ?? name;
        DiagLog($"Apply('{ActivePalette}'): {newDict.Count} keys copied; brushBefore={beforeColor} brushAfter={afterColor}");
        PaletteChanged?.Invoke(ActivePalette);
        return true;
    }

    // Set each key directly via the indexer so WPF fires per-key resource
    // change notifications. The dictionary indexer-set is the canonical
    // pattern for runtime theme switching - DynamicResource subscribers
    // re-resolve on each Set.
    private static void CopyKeysInto(ResourceDictionary source, ResourceDictionary target)
    {
        foreach (DictionaryEntry kv in source)
            target[kv.Key] = kv.Value;
    }

    private static ResourceDictionary? LoadPaletteDictionary(string name)
    {
        var entry = AvailablePalettes.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (entry?.IsUser == true && entry.FilePath != null)
            {
                using var s = File.OpenRead(entry.FilePath);
                return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(s);
            }
            else
            {
                var uri = new Uri($"/Resources/Theme/Palettes/{name}.xaml", UriKind.Relative);
                return (ResourceDictionary)Application.LoadComponent(uri);
            }
        }
        catch (Exception ex)
        {
            DiagLog($"LoadPaletteDictionary('{name}') FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Diagnostic log path: %APPDATA%\GmEcuSimulator\theme.log. Used to debug
    // hot-swap behaviour from outside the running process.
    private static readonly object _logLock = new();
    private static void DiagLog(string line)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmEcuSimulator");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "theme.log");
            lock (_logLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
