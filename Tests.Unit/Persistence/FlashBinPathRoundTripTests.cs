using Common.Persistence;
using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Guards the FlashBinPath field's round-trip through ConfigStore. Observed
// bug (2026-05-23): EcuDtoFrom didn't carry FlashBinPath back, so the WPF's
// auto-save dropped the field, the next launch ran with no flash backing,
// and PCMTec popped "Unknown Vehicle / CONDITIONS_NOT_CORRECT" because $23
// silently NRC'd $22.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FlashBinPathRoundTripTests
{
    [Fact]
    public void FlashBinPath_SurvivesEcuDtoFromAndBack()
    {
        const string Path = @"C:\some\path\to\HAEE4UY.bin";
        var dtoIn = new EcuDto
        {
            Name = "Ford PCM (capture)",
            PhysicalRequestCanId = 0x7E0,
            UsdtResponseCanId = 0x7E8,
            UudtResponseCanId = 0x5E8,
            PersonaId = "ford-uds",
            FlashBinPath = Path,
        };

        // Build a node from the DTO, then snapshot back to a DTO.
        // Skip the actual file-load by using a non-existent path - it'd
        // throw inside LoadFlashBin. Easier: use a temp file.
        var tempPath = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllBytes(tempPath, new byte[] { 0x00 });
            dtoIn.FlashBinPath = tempPath;

            var node = ConfigStore.EcuNodeFrom(dtoIn);
            Assert.Equal(tempPath, node.FlashBinPath);
            Assert.Equal("ford-uds", node.Persona.Id);

            var dtoOut = ConfigStore.EcuDtoFrom(node);
            Assert.Equal(tempPath, dtoOut.FlashBinPath);
            Assert.Equal("ford-uds", dtoOut.PersonaId);
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); } catch { }
            Core.Ecu.Personas.FordUdsPersona.LoadFlashBin((byte[]?)null);
        }
    }

    [Fact]
    public void FlashBinPath_OmittedWhenNull()
    {
        var node = new Core.Ecu.EcuNode
        {
            Name = "x",
            PhysicalRequestCanId = 0x7E0,
            UsdtResponseCanId = 0x7E8,
            UudtResponseCanId = 0x5E8,
        };
        Assert.Null(node.FlashBinPath);
        var dto = ConfigStore.EcuDtoFrom(node);
        Assert.Null(dto.FlashBinPath);
    }
}
