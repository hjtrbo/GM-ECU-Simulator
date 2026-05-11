using Common.Replay;
using Core.Replay;

namespace GmEcuSimulator.ViewModels;

// Row in the Bin Replay tab's channel grid. Reads its current value through
// the coordinator at refresh time (driven from MainWindow's 100 ms timer)
// rather than holding a cached value, so any state change is reflected on
// the next paint without needing to subscribe to coordinator events.
public sealed class BinReplayChannelViewModel : NotifyPropertyChangedBase
{
    private readonly BinReplayCoordinator coord;
    private string liveValue = "—";

    public BinReplayChannelViewModel(BinChannelHeader header, int channelIndex, BinReplayCoordinator coord)
    {
        Header = header;
        ChannelIndex = channelIndex;
        this.coord = coord;
        var template = NodeTypeMapper.FromNodeType(header.NodeType);
        EcuLabel = template == null
            ? "(unmapped)"
            : $"{template.Name} (0x{template.PhysicalRequestCanId:X3} → 0x{template.UsdtResponseCanId:X3})";
        AddressHex = header.Address <= 0xFFFF ? $"0x{header.Address:X4}" : $"0x{header.Address:X8}";
    }

    public BinChannelHeader Header { get; }
    public int ChannelIndex { get; }
    public string Name => Header.Name;
    public string Unit => Header.Unit;
    public string AddressHex { get; }
    public string EcuLabel { get; }
    public int SizeBytes => Header.SizeBytes;

    public string LiveValue
    {
        get => liveValue;
        private set => SetField(ref liveValue, value);
    }

    public void RefreshLive(double busNowMs)
    {
        // Coordinator returns the engineering value (the same value the wire
        // codec would scale-and-encode). Format with the channel's natural
        // precision: smaller scalar -> more digits.
        double v = coord.Sample(ChannelIndex, busNowMs);
        LiveValue = v.ToString(Header.Scalar < 0.5 ? "F2" : "F1");
    }
}
