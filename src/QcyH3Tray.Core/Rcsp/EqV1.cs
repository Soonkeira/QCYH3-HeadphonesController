namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// EQ v1 单段（cmd 0x20，线上 6 字节/段）。
/// </summary>
/// <param name="Frequency">中心频率 Hz（u16LE）。</param>
/// <param name="Gain">增益，1/100-dB（i16LE；写入时钳到 ±1270，即 ±12.7 dB）。</param>
/// <param name="Q">Q 值 ×100（u16LE，如 120 = Q1.20）。注意：真机读回里该字段此前被猜测为
/// 「参考增益」，Quicky 权威实现（<c>internal/response/response.go</c> ParseEQV1 与
/// <c>internal/command/EQ.go</c> NewEQV1Command）证实为 Q × 100。</param>
public sealed record EqV1Band(ushort Frequency, int Gain, int Q);

/// <summary>
/// EQ v1 表（读响应 <c>[0x20][declaredLen][eqType][masterGain i16LE][段 × 6B]</c> 的解析结果）。
/// </summary>
/// <param name="EqType">EQ 预设索引（响应第 3 字节；真机基线为 0x02）。</param>
/// <param name="MasterGain">主增益，1/100-dB（i16LE；真机基线 0xFDDA = -550 = -5.5 dB）。</param>
/// <param name="Bands">按线上顺序的频段列表（真机 7 段：55/220/500/1000/1800/2800/7500 Hz）。</param>
public sealed record EqV1Info(byte EqType, int MasterGain, IReadOnlyList<EqV1Band> Bands);

/// <summary>
/// EQ v1 协议（cmd 0x20，读写双向同 command ID），以 Quicky 为权威蓝本：
/// <list type="bullet">
/// <item>读（Request Data）：<c>FE 01 20</c> → <c>[0x20][len][eqType][masterGain i16LE]
/// [{freq u16LE, gain i16LE, q u16LE} × N]</c>（Quicky <c>internal/response/response.go</c> ParseEQV1、
/// <c>docs/protocol.md</c>「0x20 — EQ Parameters (v1 Read)」）；</item>
/// <item>写（SetEQV1）：<c>20 [len=N×6+3] [eqIndex] [masterGain i16LE] [段 × 6B]</c>，
/// 段增益钳到 ±1270（Quicky <c>internal/command/EQ.go</c> NewEQV1Command 的 clampGain、
/// <c>docs/protocol.md</c>「Parametric EQ Write (v1, cmd 0x20)」）。</item>
/// </list>
/// 真机（AA:BB:CC:DD:EE:FF，2026-10）对 v2 读 <c>FE 01 22</c> 回空载荷，对 v1 读稳定回
/// 47 字节（7 段），与上述格式逐字段吻合。
/// </summary>
public static class EqV1
{
    /// <summary>EQ v1 command ID（读 = FE 01 20，写 = 20 …，同 ID）。</summary>
    public const byte Cmd = 0x20;

    /// <summary>线上每段字节数：freq u16LE + gain i16LE + q u16LE。</summary>
    public const int BytesPerBand = 6;

    /// <summary>声明长度覆盖的头字节数：[eqType][masterGain 2B]，段数据紧随其后。</summary>
    public const int HeaderLength = 3;

    /// <summary>合法 v1 表的最少段数（真机响应恰为 7 段 = 47B）。</summary>
    public const int MinBandCount = 7;

    /// <summary>段增益写入上限 ±1270（±12.7 dB），Quicky clampGain 同值。</summary>
    public const int GainLimit = 1270;

    /// <summary>
    /// 解析 CUSTOM 响应 params（含 <c>0x20</c> 回声与声明长度字节）。守卫与 v2 读取一致：
    /// 回声必须为 0x20，且 <c>min(声明长度, 实际长度) ≥ 3 + 7×6</c>（至少 7 段），
    /// 否则返回 <see langword="null"/>（不抛异常）。
    /// </summary>
    public static EqV1Info? ParseResponse(byte[] parameters)
    {
        if (parameters.Length < 2 || parameters[0] != Cmd)
        {
            return null;
        }

        int declaredLength = parameters[1];
        int available = Math.Min(declaredLength, parameters.Length - 2);
        if (available < HeaderLength + MinBandCount * BytesPerBand)
        {
            return null;
        }

        byte eqType = parameters[2];
        int masterGain = (short)(parameters[3] | (parameters[4] << 8));
        int bandCount = (available - HeaderLength) / BytesPerBand;
        const int BandDataStart = 2 + HeaderLength;   // [cmd 回声][声明长度] 前缀 + [eqType][masterGain 2B]
        var bands = new List<EqV1Band>(bandCount);
        for (int i = 0; i < bandCount; i++)
        {
            int offset = BandDataStart + i * BytesPerBand;
            ushort frequency = (ushort)(parameters[offset] | (parameters[offset + 1] << 8));
            int gain = (short)(parameters[offset + 2] | (parameters[offset + 3] << 8));
            int q = (ushort)(parameters[offset + 4] | (parameters[offset + 5] << 8));
            bands.Add(new EqV1Band(frequency, gain, q));
        }

        return new EqV1Info(eqType, masterGain, bands);
    }

    /// <summary>
    /// 构建 v1 写入载荷（<c>20 [len][eqIndex][masterGain i16LE][段 × 6B]</c>），与 Quicky
    /// NewEQV1Command 逐字节一致：段增益钳到 ±<see cref="GainLimit"/>，段按传入顺序原样写。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="masterGain"/> 超出 i16。</exception>
    /// <exception cref="ArgumentException"><paramref name="bands"/> 为空或总长超出 1 字节声明长度。</exception>
    public static byte[] BuildWritePayload(byte eqIndex, int masterGain, IReadOnlyList<EqV1Band> bands)
    {
        if (masterGain is < short.MinValue or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(masterGain), masterGain, "masterGain out of i16 range.");
        }
        if (bands.Count == 0)
        {
            throw new ArgumentException("EQ v1 写入至少需要 1 个频段。", nameof(bands));
        }
        int dataLength = HeaderLength + bands.Count * BytesPerBand;
        if (dataLength > 0xFF)
        {
            throw new ArgumentException($"EQ v1 写入段数过多（{bands.Count} 段超出 1 字节声明长度）。", nameof(bands));
        }

        var payload = new byte[2 + dataLength];
        payload[0] = Cmd;
        payload[1] = (byte)dataLength;
        payload[2] = eqIndex;
        payload[3] = (byte)((short)masterGain & 0xFF);
        payload[4] = (byte)(((short)masterGain >> 8) & 0xFF);

        for (int i = 0; i < bands.Count; i++)
        {
            EqV1Band band = bands[i];
            if (band.Q is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(bands), band.Q, "Q out of u16 range.");
            }

            int gain = Math.Clamp(band.Gain, -GainLimit, GainLimit);
            int offset = 5 + i * BytesPerBand;
            payload[offset] = (byte)(band.Frequency & 0xFF);
            payload[offset + 1] = (byte)(band.Frequency >> 8);
            payload[offset + 2] = (byte)(gain & 0xFF);
            payload[offset + 3] = (byte)((gain >> 8) & 0xFF);
            payload[offset + 4] = (byte)(band.Q & 0xFF);
            payload[offset + 5] = (byte)(band.Q >> 8);
        }
        return payload;
    }
}

/// <summary>
/// 10 段预设值（<see cref="EqBands"/> 体系）与 7 段 v1 表的频段映射：按频率匹配，
/// 4500/10000/22000 Hz 三段在真机 v1 表中不存在，转换时丢弃/补 0。
/// </summary>
public static class EqV1BandMap
{
    /// <summary>真机 v1 表出现的 7 个频段（Hz），与 <see cref="EqBands.Frequencies"/> 的子集。</summary>
    public static readonly ushort[] Frequencies = [55, 220, 500, 1000, 1800, 2800, 7500];

    /// <summary>v1 表段数（= <see cref="Frequencies"/>.Length）。</summary>
    public const int SevenBandCount = 7;

    /// <summary>
    /// 10 段增益 → 7 段：按频率匹配取值，4500/10000/22000 三段丢弃；
    /// 缺失/越界输入按 0 处理。
    /// </summary>
    public static int[] TenToSeven(IReadOnlyList<int> tenBandGains)
    {
        var result = new int[SevenBandCount];
        for (int i = 0; i < SevenBandCount; i++)
        {
            int index = Array.IndexOf(EqBands.Frequencies, Frequencies[i]);
            result[i] = index >= 0 && index < tenBandGains.Count ? tenBandGains[index] : 0;
        }
        return result;
    }

    /// <summary>7 段增益 → 10 段：按频率匹配回填，4500/10000/22000 三段补 0。</summary>
    public static int[] SevenToTen(IReadOnlyList<int> sevenBandGains)
    {
        var result = new int[EqBands.BandCount];
        for (int i = 0; i < SevenBandCount; i++)
        {
            int index = Array.IndexOf(EqBands.Frequencies, Frequencies[i]);
            if (index >= 0 && i < sevenBandGains.Count)
            {
                result[index] = sevenBandGains[i];
            }
        }
        return result;
    }
}
