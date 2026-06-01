using Common;
using Core.Persistence;
using Xunit;

namespace EcuSimulator.Tests.AppModes;

public sealed class ConfigStorePathTests
{
    [Theory]
    [InlineData(AppMode.EcuSimulator)]
    [InlineData(AppMode.DpsSimulator)]
    public void PathForMode_ResolvesToLocalAppDataLastUsedFile(AppMode mode)
    {
        var path = ConfigStore.PathForMode(mode);

        // All modes share the single rolling "last used" auto-state file
        Assert.Equal("lastused.mode.json", Path.GetFileName(path));

        // Lives under %LOCALAPPDATA%\GmEcuSimulator\config
        var expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator", "config");
        Assert.Equal(expectedDir, Path.GetDirectoryName(path));
    }
}
