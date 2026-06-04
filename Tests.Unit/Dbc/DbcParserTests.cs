using Common.Dbc;
using Xunit;

namespace EcuSimulator.Tests.Dbc;

// Parser coverage against the two real DBCs shipped in resources/ plus a couple of inline cases for
// constructs those files don't exercise (extended ids, attribute-line skipping).
public sealed class DbcParserTests
{
    private static string LocateResource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "resources", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate resources/" + fileName);
    }

    private static DbcDatabase ParseResource(string fileName)
        => DbcParser.Parse(File.ReadAllText(LocateResource(fileName)));

    [Fact]
    public void Ford_Parses_EngineSpeedMessageAndSignal()
    {
        var db = ParseResource("FG_Falcon_HighSpeed_CAN.dbc");
        Assert.Equal(69, db.Messages.Count);

        var msg = db.Messages.Single(m => m.Id == 519);
        Assert.Equal("PCM_MSG_6", msg.Name);
        Assert.Equal(8, msg.Dlc);
        Assert.Equal("Vector__XXX", msg.Transmitter);

        var rpm = msg.Signals.Single(s => s.Name == "Engine_Speed");
        Assert.Equal(7, rpm.StartBit);
        Assert.Equal(16, rpm.Length);
        Assert.Equal(DbcByteOrder.Motorola, rpm.ByteOrder);
        Assert.False(rpm.Signed);
        Assert.Equal(0.25, rpm.Scale, 6);
        Assert.Equal("rpm", rpm.Unit);
    }

    [Fact]
    public void Ford_Parses_SignedSignal()
    {
        var db = ParseResource("FG_Falcon_HighSpeed_CAN.dbc");
        var roc = db.Messages.Single(m => m.Id == 519).Signals.Single(s => s.Name == "Engine_Speed_ROC");
        Assert.True(roc.Signed);
        Assert.Equal(DbcByteOrder.Motorola, roc.ByteOrder);
    }

    [Fact]
    public void Parses_MultiplexTokens()
    {
        // Neither resources DBC uses multiplexing, so cover the M / m<n> grammar inline.
        const string dbc =
            "BO_ 1616 Cfg: 8 ECM\n" +
            " SG_ CAN_HEADER M : 7|8@0+ (1,0) [0|255] \"\" Vector__XXX\n" +
            " SG_ DISPL_LITERS m16 : 15|8@0+ (1,0) [0|255] \"L\" Vector__XXX\n";
        var msg = Assert.Single(DbcParser.Parse(dbc).Messages);
        Assert.True(msg.Signals.Single(s => s.Name == "CAN_HEADER").IsMultiplexor);
        Assert.Equal(16, msg.Signals.Single(s => s.Name == "DISPL_LITERS").MultiplexedOn);
    }

    [Fact]
    public void GlobalA_Parses_WithoutAttributeLinesBecomingMessages()
    {
        // 346 BO_ lines; the 30k lines of VAL_TABLE_ / BU_ / BA_DEF_ / CM_ must all be skipped.
        var db = ParseResource("GlobalA - HS.dbc");
        Assert.Equal(346, db.Messages.Count);
        Assert.All(db.Messages, m => Assert.InRange(m.Dlc, 0, 64));
        Assert.Contains(db.Messages, m => m.CycleTimeMs is > 0);
        Assert.Contains(db.Messages, m => m.Transmitter == "ECM_HS");
    }

    [Fact]
    public void ExtendedId_FlagIsMaskedOff()
    {
        // 0x80000180 = 2147484032: extended frame, arbitration id 0x180.
        const string dbc = "BO_ 2147484032 Ext_Msg: 8 ECM\n SG_ X : 7|8@0+ (1,0) [0|255] \"\" Vector__XXX\n";
        var db = DbcParser.Parse(dbc);
        var msg = Assert.Single(db.Messages);
        Assert.True(msg.Extended);
        Assert.Equal(0x180u, msg.Id);
    }

    [Fact]
    public void UnknownLines_AreToleratedAndSkipped()
    {
        const string dbc =
            "VAL_TABLE_ vt 1 \"on\" 0 \"off\" ;\n" +
            "BO_ 256 Msg: 2 ECM\n" +
            " SG_ A : 7|8@0+ (1,0) [0|255] \"\" Vector__XXX\n" +
            "CM_ BO_ 256 \"a comment\";\n" +
            "BA_ \"GenMsgCycleTime\" BO_ 256 100;\n";
        var db = DbcParser.Parse(dbc);
        var msg = Assert.Single(db.Messages);
        Assert.Equal("Msg", msg.Name);
        Assert.Equal(100, msg.CycleTimeMs);
        Assert.Single(msg.Signals);
    }
}
