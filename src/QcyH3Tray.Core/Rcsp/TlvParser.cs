namespace QcyH3Tray.Core.Rcsp;

/// <summary>A parsed TLV entry: <c>[L:1B][T:1B][V: L-1 bytes]</c> — L counts the type byte itself.</summary>
public readonly record struct TlvEntry(byte Type, byte[] Value);

/// <summary>
/// Parser for the RCSP TLV stream, ported from Python reference <c>rcsp.py</c>
/// (<c>parse_tlv</c>): each entry is <c>[L][T][V×(L-1)]</c> where L includes T itself.
/// Parsing stops at the first truncated or out-of-bounds entry (matching Python's
/// <c>break</c> semantics); it never throws.
/// </summary>
public static class TlvParser
{
    public static List<TlvEntry> Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<TlvEntry>();
        int i = 0;
        int n = data.Length;
        while (i < n)
        {
            if (i + 1 >= n)
            {
                break;
            }

            int length = data[i];
            if (length < 1 || i + 1 + length > n)
            {
                break;
            }

            byte type = data[i + 1];
            var value = data.Slice(i + 2, length - 1).ToArray();
            entries.Add(new TlvEntry(type, value));
            i += 1 + length;
        }

        return entries;
    }

    /// <inheritdoc cref="Parse(ReadOnlySpan{byte})"/>
    public static List<TlvEntry> Parse(byte[] data) => Parse(data.AsSpan());
}
