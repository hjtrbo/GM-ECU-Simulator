using System.Globalization;
using System.Text.RegularExpressions;

namespace Common.Dbc;

// Minimal, tolerant DBC parser. Recognises BO_ (messages), SG_ (signals incl. the M / m<n>
// multiplex token), and BA_ "GenMsgCycleTime" (transmit period); every other line type
// (VAL_TABLE_, VAL_, CM_, BA_DEF_, BU_, NS_, BS_, ...) is skipped silently. That is enough to drive
// the broadcast feature and keeps the parser robust against the dozens of attribute constructs real
// GM/Ford DBCs carry (GlobalA - HS.dbc has 30k lines of them).
//
// Numbers are parsed with InvariantCulture so factors like "6.1035e-05" and "0.737463127" survive
// regardless of the machine's locale.
public static class DbcParser
{
    // BO_ <id> <name>: <dlc> <transmitter>
    // Name is a C identifier; the colon may sit against the name ("PCM_Dash_1:") or be spaced.
    private static readonly Regex MessageRe = new(
        @"^BO_\s+(?<id>\d+)\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?<dlc>\d+)\s+(?<tx>\S+)\s*$",
        RegexOptions.Compiled);

    //  SG_ <name> [M|m<n>] : <start>|<len>@<order><sign> (<factor>,<offset>) [<min>|<max>] "<unit>" <receivers>
    private static readonly Regex SignalRe = new(
        @"^\s*SG_\s+(?<name>[A-Za-z_]\w*)\s*(?<mux>[Mm]\d*)?\s*:\s*" +
        @"(?<start>\d+)\|(?<len>\d+)@(?<order>[01])(?<sign>[+-])\s*" +
        @"\(\s*(?<factor>[^,]+?)\s*,\s*(?<offset>[^)]+?)\s*\)\s*" +
        @"\[\s*(?<min>[^|]*?)\s*\|\s*(?<max>[^\]]*?)\s*\]\s*" +
        @"""(?<unit>[^""]*)""",
        RegexOptions.Compiled);

    // BA_ "GenMsgCycleTime" BO_ <id> <ms> ;
    private static readonly Regex CycleTimeRe = new(
        @"^BA_\s+""GenMsgCycleTime""\s+BO_\s+(?<id>\d+)\s+(?<ms>\d+)\s*;",
        RegexOptions.Compiled);

    private const uint ExtendedFlag = 0x8000_0000u;
    private const uint ExtendedMask = 0x1FFF_FFFFu;

    public static DbcDatabase Parse(string text)
    {
        var db = new DbcDatabase();
        // Index by the RAW DBC id (extended flag still set) so a later BA_ GenMsgCycleTime line,
        // which uses that same raw id, resolves the message it belongs to.
        var byRawId = new Dictionary<uint, DbcMessage>();
        DbcMessage? current = null;

        foreach (var rawLine in SplitLines(text))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("BO_ ", StringComparison.Ordinal))
            {
                var m = MessageRe.Match(line);
                if (!m.Success) { current = null; continue; }
                uint rawId = uint.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
                var msg = new DbcMessage
                {
                    Id = rawId & ExtendedMask,
                    Extended = (rawId & ExtendedFlag) != 0,
                    Name = m.Groups["name"].Value,
                    Dlc = int.Parse(m.Groups["dlc"].Value, CultureInfo.InvariantCulture),
                    Transmitter = m.Groups["tx"].Value,
                };
                db.Messages.Add(msg);
                byRawId[rawId] = msg;
                current = msg;
                continue;
            }

            if (trimmed.StartsWith("SG_ ", StringComparison.Ordinal))
            {
                // Signals belong to the most recent BO_. A stray SG_ with no message is skipped.
                if (current == null) continue;
                var sig = ParseSignal(line);
                if (sig != null) current.Signals.Add(sig);
                continue;
            }

            if (trimmed.StartsWith("BA_ \"GenMsgCycleTime\"", StringComparison.Ordinal))
            {
                var m = CycleTimeRe.Match(trimmed);
                if (!m.Success) continue;
                uint rawId = uint.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
                if (byRawId.TryGetValue(rawId, out var msg))
                    msg.CycleTimeMs = int.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            // A non-SG_ line ends the current message's signal block (attribute / comment sections
            // follow the message list). Leaving `current` set is harmless because only SG_ lines
            // consume it, but clearing on a clearly-unrelated keyword keeps intent obvious.
        }

        return db;
    }

    private static DbcSignal? ParseSignal(string line)
    {
        var m = SignalRe.Match(line);
        if (!m.Success) return null;

        string mux = m.Groups["mux"].Value;
        bool isMux = mux == "M";
        int? muxOn = mux.Length > 1 && mux[0] == 'm'
            ? int.Parse(mux.AsSpan(1), CultureInfo.InvariantCulture)
            : (int?)null;

        return new DbcSignal
        {
            Name = m.Groups["name"].Value,
            StartBit = int.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture),
            Length = int.Parse(m.Groups["len"].Value, CultureInfo.InvariantCulture),
            ByteOrder = m.Groups["order"].Value == "0" ? DbcByteOrder.Motorola : DbcByteOrder.Intel,
            Signed = m.Groups["sign"].Value == "-",
            Scale = ParseDouble(m.Groups["factor"].Value, 1.0),
            Offset = ParseDouble(m.Groups["offset"].Value, 0.0),
            Min = ParseDouble(m.Groups["min"].Value, 0.0),
            Max = ParseDouble(m.Groups["max"].Value, 0.0),
            Unit = m.Groups["unit"].Value,
            IsMultiplexor = isMux,
            MultiplexedOn = muxOn,
        };
    }

    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static IEnumerable<string> SplitLines(string text)
    {
        // Handle \n, \r\n and bare \r without allocating a split array for a 30k-line file.
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                yield return text.Substring(start, i - start);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text.Substring(start);
    }
}
