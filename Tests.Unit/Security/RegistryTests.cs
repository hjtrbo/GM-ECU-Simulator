using Core.Security;
using Xunit;

namespace EcuSimulator.Tests.Security;

public sealed class RegistryTests
{
    [Fact]
    public void BuiltIn_NotImplemented_Resolves()
    {
        var module = SecurityModuleRegistry.Create("gmw3110-2010-not-implemented");
        Assert.NotNull(module);
        Assert.Equal("gmw3110-2010-not-implemented", module!.Id);
    }

    [Fact]
    public void UnknownId_ReturnsNull()
    {
        Assert.Null(SecurityModuleRegistry.Create("definitely-not-registered-12345"));
    }

    [Fact]
    public void NullId_ReturnsNull()
    {
        Assert.Null(SecurityModuleRegistry.Create(null));
    }

    [Fact]
    public void KnownIds_ContainsBuiltIn()
    {
        Assert.Contains("gmw3110-2010-not-implemented", SecurityModuleRegistry.KnownIds);
    }
}
