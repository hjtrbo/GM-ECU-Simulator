using Common.PassThru;
using Common.Wire;

namespace EcuSimulator.Tests.Common;

public class IpcWireTests
{
    [Fact]
    public void U32_RoundTrips()
    {
        var w = new IpcWriter();
        w.WriteU32(0xDEADBEEF);
        var r = new IpcReader(w.AsSpan());
        Assert.Equal(0xDEADBEEFu, r.ReadU32());
    }

    [Fact]
    public void U32_LittleEndian()
    {
        var w = new IpcWriter();
        w.WriteU32(0x01020304);
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, w.ToArray());
    }

    [Fact]
    public void StringU16_RoundTrips()
    {
        var w = new IpcWriter();
        w.WriteStringU16Length("hello");
        var r = new IpcReader(w.AsSpan());
        Assert.Equal("hello", r.ReadStringU16Length());
    }

    [Fact]
    public void PassThruMsg_RoundTrips()
    {
        var msg = new PassThruMsg
        {
            ProtocolID = ProtocolID.CAN,
            RxStatus = RxStatus.TX_INDICATION,
            TxFlags = TxFlag.ISO15765_FRAME_PAD,
            Timestamp = 12345,
            ExtraDataIndex = 0,
            Data = new byte[] { 0x00, 0x00, 0x02, 0x41, 0x03, 0x22, 0x12, 0x34 },
        };
        var w = new IpcWriter();
        w.WritePassThruMsg(msg);
        var r = new IpcReader(w.AsSpan());
        var got = r.ReadPassThruMsg();
        Assert.Equal(msg.ProtocolID, got.ProtocolID);
        Assert.Equal(msg.RxStatus, got.RxStatus);
        Assert.Equal(msg.TxFlags, got.TxFlags);
        Assert.Equal(msg.Timestamp, got.Timestamp);
        Assert.Equal(msg.Data, got.Data);
    }

    [Fact]
    public void Reader_ThrowsOnUnderflow()
    {
        Assert.Throws<InvalidDataException>(ReadOneU32FromSingleByte);
    }

    private static void ReadOneU32FromSingleByte()
    {
        var r = new IpcReader(new byte[] { 0x01 });
        _ = r.ReadU32();
    }
}
