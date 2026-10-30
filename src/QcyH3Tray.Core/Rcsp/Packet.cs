namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// RCSP (Remote Control Service Protocol) frame — JieLi BLE protocol.
/// Ported byte-for-byte from the Python reference <c>rcsp.py</c> (Packet dataclass).
/// <para>
/// Frame layout:
/// <code>
/// [FE DC BA] header (3B)
/// [flag]     1B: bit7 = type (0=response, 1=command), bit6 = hasResponse
/// [opCode]   1B
/// [paramLen] 2B big-endian
/// [status]   1B - present ONLY if type = response
/// [opCodeSn] 1B - sequence number
/// [xmOpCode] 1B - present ONLY if opCode == 1
/// [params]   N bytes
/// [EF]       footer (1B)
/// </code>
/// Total length = 7 + paramLen + 1.
/// </para>
/// </summary>
public sealed class Packet
{
    public const byte TypeResponse = 0;
    public const byte TypeCommand = 1;

    /// <summary>opCode 1 (DATA) is the only op-code carrying an extra xmOpCode byte.</summary>
    private const byte DataOpCode = 0x01;

    /// <summary>Frame header bytes FE DC BA.</summary>
    public static ReadOnlySpan<byte> Header => HeaderBytes;

    /// <summary>Frame footer byte EF.</summary>
    public const byte Footer = 0xEF;

    internal static readonly byte[] HeaderBytes = [0xFE, 0xDC, 0xBA];

    public Packet(byte opCode, byte type = TypeCommand, byte hasResponse = 1, byte status = 0,
        byte sn = 0, byte? xmOpCode = null, byte[]? parameters = null)
    {
        OpCode = opCode;
        Type = type;
        HasResponse = hasResponse;
        Status = status;
        Sn = sn;
        XmOpCode = xmOpCode;
        Params = parameters ?? [];
    }

    public byte OpCode { get; set; }

    /// <summary>0 = response, 1 = command (<see cref="TypeResponse"/> / <see cref="TypeCommand"/>).</summary>
    public byte Type { get; set; }

    /// <summary>1 = the device is expected to answer, 0 = fire and forget.</summary>
    public byte HasResponse { get; set; }

    /// <summary>Status byte, present in encoded frames only for responses.</summary>
    public byte Status { get; set; }

    /// <summary>Sequence number (opCodeSn). 0 means "assign automatically" when sent via H3Client.</summary>
    public byte Sn { get; set; }

    /// <summary>Extra op-code byte inserted after sn when <see cref="OpCode"/> equals 1 (DATA).</summary>
    public byte? XmOpCode { get; set; }

    public byte[] Params { get; set; }

    /// <summary>
    /// Mirrors Python <c>Packet.encode</c>: flag = (type &amp; 1) &lt;&lt; 7 | (hasResponse &amp; 1) &lt;&lt; 6;
    /// body = [status?][sn][xmOpCode?][params]; paramLen = body length (big-endian 2B).
    /// </summary>
    public byte[] Encode()
    {
        var flag = (byte)(((Type & 1) << 7) | ((HasResponse & 1) << 6));

        int bodyLength = 1 + Params.Length
            + (Type == TypeResponse ? 1 : 0)
            + (OpCode == DataOpCode && XmOpCode is not null ? 1 : 0);
        var result = new byte[HeaderBytes.Length + 4 + bodyLength + 1];

        int i = 0;
        HeaderBytes.CopyTo(result, i);
        i += HeaderBytes.Length;
        result[i++] = flag;
        result[i++] = OpCode;
        result[i++] = (byte)((bodyLength >> 8) & 0xFF);
        result[i++] = (byte)(bodyLength & 0xFF);
        if (Type == TypeResponse)
        {
            result[i++] = Status;
        }
        result[i++] = Sn;
        if (OpCode == DataOpCode && XmOpCode is byte xm)
        {
            result[i++] = xm;
        }
        Params.CopyTo(result, i);
        i += Params.Length;
        result[i] = Footer;
        return result;
    }

    /// <summary>
    /// Mirrors Python <c>Packet.decode</c>: scans for the header anywhere in <paramref name="data"/>,
    /// validates length and footer. Returns <see langword="null"/> (never throws) for garbage,
    /// truncated frames, or a wrong footer.
    /// </summary>
    public static Packet? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        int idx = data.IndexOf(Header);
        if (idx < 0)
        {
            return null;
        }

        data = data[idx..];
        if (data.Length < 8)
        {
            return null;
        }

        byte flag = data[3];
        byte opCode = data[4];
        int paramLength = (data[5] << 8) | data[6];
        int total = 7 + paramLength + 1;
        if (data.Length < total)
        {
            return null;
        }

        if (data[total - 1] != Footer)
        {
            return null;
        }

        var type = (byte)((flag >> 7) & 1);
        var hasResponse = (byte)((flag >> 6) & 1);

        int i = 7;
        byte status = 0;
        // Bounds checks keep the decoder total: Python would raise IndexError on the same
        // malformed frames, the C# port must return null instead.
        if (type == TypeResponse)
        {
            if (i >= total - 1)
            {
                return null;
            }
            status = data[i];
            i++;
        }

        if (i >= total - 1)
        {
            return null;
        }
        byte sn = data[i];
        i++;

        byte? xm = null;
        if (opCode == DataOpCode)
        {
            if (i >= total - 1)
            {
                return null;
            }
            xm = data[i];
            i++;
        }

        int paramsLength = Math.Max(total - 1 - i, 0);
        var parameters = data.Slice(i, paramsLength).ToArray();

        return new Packet(opCode, type, hasResponse, status, sn, xm, parameters);
    }

    /// <inheritdoc cref="Decode(ReadOnlySpan{byte})"/>
    public static Packet? Decode(byte[] data) => Decode(data.AsSpan());
}
