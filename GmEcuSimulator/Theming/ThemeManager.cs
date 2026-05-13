using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace GmEcuSimulator.Theming;

// Runtime palette switcher.
//
// Each palette under /Resources/Theme/Palettes/<Name>.xaml declares the same
// set of brush keys with palette-specific colours. The startup palette is
// merged via Brushes.xaml so every {StaticResource ...} reference in control
// templates resolves to a live SolidColorBrush instance at parse time.
//
// To switch palettes, Apply() loads the target palette dictionary and copies
// the new Colors onto the EXISTING brush instances already in
// Application.Resources. SolidColorBrush.Color is a DependencyProperty -
// every templated control re-renders automatically without parsing or
// merging anything. LinearGradientBrushes (currently just Chrome.GradientBrush)
// are handled by stop-by-stop colour copy.
//
// "Plug-in" model: built-in palettes ship as embedded XAML resources.
// User palettes can be dropped into %APPDATA%\GmEcuSimulator\Palettes\ as
// loose .xaml files and they appear in AvailablePalettes alongside the
// built-ins.
public static class ThemeManager
{
    public sealed record PaletteEntry(string Name, string DisplayName, string? FilePath, bool IsUser);

    private const string DefaultPaletteName = "Midnight";

    private static readonly string[] BuiltIns =
    {
        "Midnight",
        "Graphite",
        "Solarized",
        "Daylight",
    };

    public static IReadOnlyList<PaletteEntry> AvailablePalettes { get; private set; } = Array.Empty<PaletteEntry>();
    public static string ActivePalette { get; private set; } = DefaultPaletteName;
    public static event Action<string>? PaletteChanged;

    /// <summary>
    /// Resolves the user palette directory: %APPDATA%\GmEcuSimulator\Palettes\.
    /// Created on first call so the user has a place to drop loose .xaml files.
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
    /// the live AvailablePalettes list. Cheap; call on startup and whenever
    /// the user adds a palette file.
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
            // Directory enumeration can fail (perms, missing drive); the
            // built-ins still surface so the menu never goes empty.
        }

        AvailablePalettes = list;
    }

    /// <summary>
    /// Switches the live UI to the named palette. Returns false silently
    /// if the palette can't be located or loaded - the caller's UI stays
    /// on the current palette and no exception escapes.
    /// </summary>
    public static bool Apply(string name)
    {
        var app = Application.Current;
        if (app == null) return false;

        var entry = AvailablePalettes.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        ResourceDictionary? source;
        try
        {
            if (entry?.IsUser == true && entry.FilePath != null)
            {
                // Loose-file palette: parse from disk.
                using var s = File.OpenRead(entry.FilePath);
                source = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(s);
            }
            else
            {
                // Built-in palette: pack URI under the running assembly.
                var uri = new Uri($"/Resources/Theme/Palettes/{name}.xaml", UriKind.Relative);
                source = (ResourceDictionary)Application.LoadComponent(uri);
            }
        }
        catch
        {
            return false;
        }

        if (source == null) return false;

        // Mutate brushes in place. Walk the target palette's keys, find the
        // matching brush instance in App.Resources, and copy colour values
        // onto it. Skip frozen brushes (treat as unchangeable - shouldn't
        // happen with our XAML but defensive).
        foreach (DictionaryEntry kv in source)
        {
            var existing = app.Resources[kv.Key];
            switch (existing)
            {
                case SolidColorBrush eb when kv.Value is SolidColorBrush nb && !eb.IsFrozen:
                    eb.Color = nb.Color;
                    break;
                case LinearGradientBrush eg when kv.Value is LinearGradientBrush ng && !eg.IsFrozen:
                    int n = Math.Min(eg.GradientStops.Count, ng.GradientStops.Count);
                    for (int i = 0; i < n; i++)
                    {
                        var stop = eg.GradientStops[i];
                        if (!stop.IsFrozen) stop.Color = ng.GradientStops[i].Color;
                    }
                    break;
                default:
                    // Color resource (struct) or new key not in current dict:
                    // replace outright. Existing brushes wrapping it stay
                    // mutated above; downstream Color lookups see the new value.
                    if (kv.Value is Color)
                        app.Resources[kv.Key] = kv.Value;
                    break;
            }
        }

        ActivePalette = entry?.Name ?? name;
        PaletteChanged?.Invoke(ActivePalette);
        return true;
    }
}
