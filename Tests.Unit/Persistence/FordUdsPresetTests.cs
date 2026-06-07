using Common.Persistence;
using Core.Ecu.Personas;
using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Guards the Ford UDS preset the user actually loads at
// %LocalAppData%\GmEcuSimulator\config\ford_uds.mode.json so we catch schema
// drift before the user has to: the tests round-trip it through
// ConfigSerializer + ConfigStore. This is the live file File > Open points at;
// keeping the guard on the real artefact (not a stale repo copy) is the whole
// point - a copy in the repo root drifted out of sync once already.
//
// History: the first version of this preset was written with PascalCase
// keys ("Version", "Ecus", "Name", "PersonaId") and version=1. The serializer
// is configured with JsonNamingPolicy.CamelCase + Version validation that
// rejects below MinSupportedVersion (1)... but cfg.Version = 1 actually
// passed the range check while the PascalCase keys silently deserialised to
// every property's default value, including Ecus=[]. The simulator then sat
// there with zero ECUs and no on-screen indication of why. Hence this test.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FordUdsPresetTests
{
    // The live preset under %LocalAppData%\GmEcuSimulator\config\ - the same
    // directory ConfigStore.ConfigDirectory resolves to, so this tracks the
    // file the running app reads, not a repo copy.
    private static string PresetPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator", "config", "ford_uds.mode.json");

    [Fact]
    public void Preset_File_Exists()
    {
        Assert.True(File.Exists(PresetPath()),
            $"Ford UDS preset missing at {PresetPath()}");
    }

    [Fact]
    public void Preset_Deserialises_With_CamelCase()
    {
        var json = File.ReadAllText(PresetPath());
        var cfg = ConfigSerializer.Deserialize(json);

        // Sanity: a PascalCase-typed file would silently land here with
        // Ecus=[], so the meaningful assertion is non-empty + persona id.
        Assert.NotNull(cfg);
        Assert.Equal(SimulatorConfig.CurrentVersion, cfg.Version);
        Assert.Single(cfg.Ecus);
        var ecu = cfg.Ecus[0];
        Assert.Equal((ushort)0x7E0, ecu.PhysicalRequestCanId);
        Assert.Equal((ushort)0x7E8, ecu.UsdtResponseCanId);
        Assert.Equal((ushort)0x5E8, ecu.UudtResponseCanId);
        Assert.Equal("ford-uds", ecu.PersonaId);
    }

    [Fact]
    public void Preset_Builds_Node_With_FordUdsPersona()
    {
        var json = File.ReadAllText(PresetPath());
        var cfg = ConfigSerializer.Deserialize(json);
        var node = ConfigStore.EcuNodeFrom(cfg.Ecus[0]);

        Assert.Same(FordUdsPersona.Instance, node.Persona);
    }
}
