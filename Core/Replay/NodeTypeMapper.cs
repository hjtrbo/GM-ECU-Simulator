namespace Core.Replay;

// Maps the sibling project's eNodeType (whose numeric values ARE the USDT
// response CAN IDs - 0x7E8=ECM, 0x7E9=TCM, 0x7EA=BCM, 0x7EB=FPCM) to the
// simulator's full ECU identity record using the OBD-II convention used
// elsewhere (request = USDT - 0x008, UUDT = USDT - 0x200). Unknown node
// types fall through to a synthetic NODE_<hex> name and the same arithmetic
// relationship.
//
// Returns null only for NodeType == 0 (eNodeType.None) — caller skips
// such channels.
public static class NodeTypeMapper
{
    public static EcuTemplate? FromNodeType(ushort nodeType)
    {
        if (nodeType == 0) return null;
        return new EcuTemplate(
            Name: NameFor(nodeType),
            PhysicalRequestCanId: (ushort)(nodeType - 0x008),
            UsdtResponseCanId: nodeType,
            UudtResponseCanId: (ushort)(nodeType - 0x200));
    }

    private static string NameFor(ushort nodeType) => nodeType switch
    {
        0x7E8 => "ECM",
        0x7E9 => "TCM",
        0x7EA => "BCM",
        0x7EB => "FPCM",
        _ => $"NODE_{nodeType:X3}",
    };
}

public sealed record EcuTemplate(
    string Name,
    ushort PhysicalRequestCanId,
    ushort UsdtResponseCanId,
    ushort UudtResponseCanId);
