namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// Hex dump helper, ported from Python reference <c>rcsp.py</c> (<c>hex_str</c>):
/// uppercase hex pairs joined with '-'.
/// </summary>
public static class HexFormat
{
    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('-');
            }
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static string ToHex(byte[] data) => ToHex(data.AsSpan());
}
