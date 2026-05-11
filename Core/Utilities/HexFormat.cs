namespace Core.Utilities;

internal static class HexFormat
{
    public static string Bytes(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
