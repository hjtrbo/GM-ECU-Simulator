using Common.Protocol;
using Core.Security;
using Core.Security.Algorithms;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Functionally identical to T43Algorithm by design (see file header comment
// on Gmw3110ProgrammingBypassAlgorithm.cs). The math is re-tested under the
// T43 name in T43AlgorithmTests; this file pins the *new* identity-level
// guarantees: registry id, dropdown surface, programming-session policy.
public sealed class Gmw3110ProgrammingBypassAlgorithmTests
{
    private const string RegistryId = "gmw3110-programming-bypass";

    [Fact]
    public void Algorithm_DeclaresBypassAllAndCorrectId()
    {
        var algo = new Gmw3110ProgrammingBypassAlgorithm();
        Assert.Equal(RegistryId, algo.Id);
        Assert.Equal(ProgrammingSessionBehavior.BypassAll, algo.ProgrammingSession);
        Assert.Equal(2, algo.SeedLength);
        Assert.Equal(2, algo.KeyLength);
        Assert.Equal(new byte[] { 1 }, algo.SupportedLevels);
    }

    [Fact]
    public void Registry_KnowsTheId_AndShowsItInTheDropdown()
    {
        // The UI's $27 algorithm dropdown is bound to SecurityModuleRegistry.KnownIds.
        Assert.Contains(RegistryId, SecurityModuleRegistry.KnownIds);
    }

    [Fact]
    public void Registry_CreatesAModuleThatWraps_ThisAlgorithm()
    {
        var module = SecurityModuleRegistry.Create(RegistryId);
        Assert.NotNull(module);
        Assert.Equal(RegistryId, module!.Id);
    }

    [Fact]
    public void EndToEnd_ProgrammingSession_ServesZeroSeedAndAcceptsAnyKey()
    {
        // This is the exact path DPS exercises against the simulator's Engine
        // ECU at $11 once $A5 $03 has flipped SecurityProgrammingShortcutActive
        // (or, equivalently here, $10 $02). The new module must yield the same
        // BypassAll behaviour the T43 module does, regardless of how the
        // algorithm name reads in the UI.
        var module = SecurityModuleRegistry.Create(RegistryId)!;
        var node = NodeFactory.CreateNode(module: module);
        var ch = NodeFactory.CreateChannel();

        // Enter programming session via $10 $02.
        Service10Handler.Handle(node, new byte[] { 0x10, 0x02 }, ch);
        Assert.Equal(new byte[] { Service.Positive(Service.InitiateDiagnosticOperation), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.SecurityProgrammingShortcutActive);

        // $27 $01 - request seed. Expect 00 00 regardless of algorithm math.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // $27 $02 00 00 - the literal 00 00 key 6Speed.T43-style testers send.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }
}
