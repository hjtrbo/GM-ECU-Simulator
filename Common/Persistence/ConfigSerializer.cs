using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Persistence;

// Single source of truth for the JSON serializer options used to read
// and write SimulatorConfig. Indented output is intentional so config
// files diff cleanly in source control.
public static class ConfigSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Serialize(SimulatorConfig cfg)
        => JsonSerializer.Serialize(cfg, Options);

    public static SimulatorConfig Deserialize(string json)
    {
        var cfg = JsonSerializer.Deserialize<SimulatorConfig>(json, Options)
                  ?? throw new InvalidDataException("Config JSON deserialised to null");
        if (cfg.Version < SimulatorConfig.MinSupportedVersion
            || cfg.Version > SimulatorConfig.CurrentVersion)
            throw new InvalidDataException(
                $"Config version not supported: file={cfg.Version}, "
                + $"supported range={SimulatorConfig.MinSupportedVersion}..{SimulatorConfig.CurrentVersion}");
        return cfg;
    }
}
