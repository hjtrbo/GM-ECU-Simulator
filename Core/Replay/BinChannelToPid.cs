using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;

namespace Core.Replay;

// Converts a bin file's channel list into a fresh ECU set on the bus.
// Channels are grouped by NodeType (eNodeType in the sibling project, whose
// values are the USDT response IDs); each unique non-zero NodeType becomes
// one ECU using the OBD-II convention from NodeTypeMapper. Each channel
// becomes a Pid whose WaveformConfig.Shape is set to FileStream, with a
// replay-waveform factory bound to the same BinReplayCoordinator + channel
// index. The user can flip the shape to a synthetic waveform later (Sin /
// Triangle / etc.) to detach that channel from the bin data, and back to
// FileStream to reattach.
//
// Channels with NodeType == 0 (eNodeType.None) are skipped - the caller can
// surface a UI warning with the count.
public static class BinChannelToPid
{
    public static BuildResult BuildEcus(
        IReadOnlyList<BinChannelHeader> headers,
        BinReplayCoordinator coordinator)
    {
        var byNode = new Dictionary<ushort, EcuNode>();
        int skipped = 0;

        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (h.NodeType == 0) { skipped++; continue; }

            if (!byNode.TryGetValue(h.NodeType, out var node))
            {
                var template = NodeTypeMapper.FromNodeType(h.NodeType);
                if (template == null) { skipped++; continue; }
                node = new EcuNode
                {
                    Name = template.Name,
                    PhysicalRequestCanId = template.PhysicalRequestCanId,
                    UsdtResponseCanId = template.UsdtResponseCanId,
                    UudtResponseCanId = template.UudtResponseCanId,
                };
                byNode[h.NodeType] = node;
            }

            node.AddPid(BuildPid(h, i, coordinator));
        }

        return new BuildResult(byNode.Values.ToArray(), skipped);
    }

    public static Pid BuildPid(BinChannelHeader header, int channelIndex, BinReplayCoordinator coordinator)
    {
        var pid = new Pid
        {
            Address = header.Address,
            Name = string.IsNullOrEmpty(header.Name) ? $"chan_{channelIndex:X4}" : header.Name,
            Size = MapSize(header.SizeBytes),
            DataType = MapDataType(header.DataTypeCode),
            // Bin's Scalar==0 typically means "raw" - prefer 1.0 so encoding
            // doesn't divide by zero on the wire.
            Scalar = header.Scalar == 0.0 ? 1.0 : header.Scalar,
            Offset = header.OffsetEng,
            Unit = header.Unit ?? "",
        };
        // Bind the bin coordinator + channel index BEFORE flipping Shape - the WaveformConfig setter rebuilds
        // the active generator and needs the factory in place to produce the ReplayWaveform.
        pid.SetReplayWaveformFactory(() => new ReplayWaveform(coordinator, channelIndex));
        pid.WaveformConfig = new WaveformConfig { Shape = WaveformShape.FileStream };
        return pid;
    }

    private static PidSize MapSize(int sizeBytes) => sizeBytes switch
    {
        1 => PidSize.Byte,
        2 => PidSize.Word,
        4 => PidSize.DWord,
        // Bin's eSize.None == 0 falls through; default to Word so the wire
        // protocol still functions (host sees a 2-byte sample).
        _ => PidSize.Word,
    };

    // Sibling eDataType: 0=None, 1=Bool, 2=Unsigned, 3=Signed, 4=Hex, 5=Ascii.
    // Only Signed maps non-trivially; the rest collapse to Unsigned, which is
    // how the wire encoder handles raw bytes anyway.
    private static PidDataType MapDataType(int code) => code switch
    {
        3 => PidDataType.Signed,
        _ => PidDataType.Unsigned,
    };

    public sealed record BuildResult(IReadOnlyList<EcuNode> Nodes, int SkippedChannels);
}
