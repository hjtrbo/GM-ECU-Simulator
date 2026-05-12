using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Security;
using Core.Security.Modules;

namespace EcuSimulator.Tests.TestHelpers;

internal static class NodeFactory
{
    public const ushort PhysReq = 0x7E0;
    public const ushort UsdtResp = 0x7E8;
    public const ushort UudtResp = 0x5E8;

    public static EcuNode CreateNode(ISecurityAccessModule? module = null)
        => new()
        {
            Name = "TestEcu",
            PhysicalRequestCanId = PhysReq,
            UsdtResponseCanId = UsdtResp,
            UudtResponseCanId = UudtResp,
            SecurityModule = module,
        };

    public static EcuNode CreateNodeWithGenericModule(FakeSeedKeyAlgorithm? algo = null)
        => CreateNode(new Gmw3110_2010_Generic(algo ?? new FakeSeedKeyAlgorithm(), id: "fake"));

    public static ChannelSession CreateChannel()
        => new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000 };
}
