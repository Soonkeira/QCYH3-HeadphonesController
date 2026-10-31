using System.Text;

namespace QcyH3Tray.Core.Rcsp;

/// <summary>一条探测的回包判定结果（eqprobe2 / CLI 实验通道共用）。</summary>
/// <param name="Hit">是否命中（拿到了想要的 EQ/属性数据）。</param>
/// <param name="Judgment">一行判定结论（人类可读，中文）。</param>
/// <param name="Interpretation">回包内容的解读（EQ 表解析 / 位图 / 属性表；无回包时为提示文案）。</param>
public sealed record ProbeVerdict(bool Hit, string Judgment, string Interpretation)
{
    /// <summary>未命中时的便捷构造。</summary>
    public static ProbeVerdict Miss(string judgment, string interpretation) => new(false, judgment, interpretation);

    /// <summary>命中时的便捷构造（与 <see cref="Hit"/> 属性同名会冲突，故取名 Pass）。</summary>
    public static ProbeVerdict Pass(string judgment, string interpretation) => new(true, judgment, interpretation);
}

/// <summary>
/// eqprobe2 二轮 EQ 探测的判定与解读辅助（纯函数，供 CLI 与单元测试共用）。
/// <para>
/// 调研结论（2026-10，三个参考仓库）：
/// </para>
/// <list type="bullet">
/// <item>Quicky（Go，199 型号库）：EQ 读走 <c>0xFE 0x01 &lt;cmd&gt;</c>「Request Data」；
/// EQ 家族 command = 0x20(v1, 6B/段) / 0x22(v2, 7B/段) / 0x46(左) / 0x47(右)；
/// 0x44 = 自定义 EQ 槽位数、0x27 = 自适应 EQ 状态。其响应体格式
/// <c>[eqType][masterGain 2B][{freq,gain,q,bandType}×N]</c> 与本仓 Python 格式
/// <c>[preset][70 FE][{freq,gain,refGain,Q}×N]</c> 不同，两种都要能判读。</item>
/// <item>qcy-menubar（Swift，H3/H3 Pro）：与本项目完全同协议同读法（FE 01 22）；
/// 另用 GATT 直读特征 0007(版本)/0008(电量)，并枚举服务内全部可读特征。</item>
/// <item>qcy-h3s-windows（Python，H3S）：FE 01 22 在 H3S 固件回 74B 全表；
/// 其 0x01..0x63 全扫描证明「固件不支持读」时表现为无帧超时，而非 status=0 空载荷。</item>
/// </list>
/// </summary>
public static class RcspProbe
{
    /// <summary>EQ v1 读（Quicky：6B/段，响应含 eqType + masterGain）。</summary>
    public const byte EqV1Cmd = 0x20;

    /// <summary>EQ v2 读（本机第一轮已试：status=0 + 空载荷）。</summary>
    public const byte EqV2Cmd = 0x22;

    /// <summary>自适应 EQ 状态读（Quicky）。</summary>
    public const byte AdaptiveEqCmd = 0x27;

    /// <summary>自定义 EQ 槽位数读（Quicky）。</summary>
    public const byte MaxEqCountCmd = 0x44;

    /// <summary>左通道 EQ 读（Quicky，v2 格式）。</summary>
    public const byte EqLeftCmd = 0x46;

    /// <summary>右通道 EQ 读（Quicky，v2 格式）。</summary>
    public const byte EqRightCmd = 0x47;

    /// <summary>EQ 家族全部 command ID（eqprobe2 实验 3 变体读与实验 6 全扫描重点标注）。</summary>
    public static readonly IReadOnlyList<byte> EqFamilyCmdIds =
        [EqV1Cmd, EqV2Cmd, AdaptiveEqCmd, MaxEqCountCmd, EqLeftCmd, EqRightCmd];

    /// <summary>全扫描覆盖的 command ID 区间上界（含），与 qcy-h3s-windows 的 0x01..0x63 一致。</summary>
    public const byte SweepMaxCmdId = 0x63;

    /// <summary>EQ 全表判定的最小 params 长度（与第一轮 eqprobe 一致：params[0]==cmd 且长度 ≥ 52）。</summary>
    public const int EqTableMinParamsLength = 52;

    /// <summary>请求 params 是否为 CUSTOM「Request Data」读（FE 01 cmd）。</summary>
    public static bool IsRequestDataRead(byte[] requestParams, out byte cmdId)
    {
        if (requestParams is [0xFE, 0x01, var cmd])
        {
            cmdId = cmd;
            return true;
        }
        cmdId = 0;
        return false;
    }

    /// <summary>
    /// 判定一条 CUSTOM 读（FE 01 cmd）的回包：
    /// <list type="bullet">
    /// <item>EQ 全表命令（0x20/0x22/0x46/0x47）：命中 = 回声命令一致且 params ≥ 52；
    /// 解读时依次尝试 Python/H3 格式（preset + 70 FE + 7B/段）与 Quicky 格式（eqType + masterGain + 7B/段）。</item>
    /// <item>0x44（槽位数）/0x27（自适应开关）：命中 = 声明长度 &gt; 0 且有数据字节。</item>
    /// <item>其它 command：命中 = 有非空数据（cmd 回声 + 声明长度 &gt; 0）。</item>
    /// </list>
    /// </summary>
    public static ProbeVerdict AnalyzeCustomRead(byte[] requestParams, Packet? response)
    {
        if (!IsRequestDataRead(requestParams, out byte cmd))
        {
            return ProbeVerdict.Miss("非 Request Data 读请求", $"请求 params = {Hex(requestParams)} 不是 FE 01 cmd 形式。");
        }

        if (response is null)
        {
            return ProbeVerdict.Miss(
                "无响应（超时，窗口内没有 sn 匹配的回包）",
                "该 command 在此固件上不可读——与 qcy-h3s-windows 全扫描中「不支持读」的表现一致（无帧、静默超时）。");
        }

        byte[] p = response.Params;
        if (p.Length == 0)
        {
            return ProbeVerdict.Miss(
                "回包 status=0 但载荷为空",
                "固件认识该 command 并应答成功，却没有携带任何数据（本机 0x22 的已知表现，区别于 H3S 的无帧超时）。");
        }

        if (p[0] != cmd)
        {
            return ProbeVerdict.Miss(
                $"回包首字节 0x{p[0]:X2} ≠ 请求命令 0x{cmd:X2}",
                $"回包不是本命令的回声（params({p.Length}B) = {Hex(p)}），按设备主动推送处理。");
        }

        return cmd switch
        {
            EqV1Cmd or EqV2Cmd or EqLeftCmd or EqRightCmd => AnalyzeEqTable(cmd, p),
            MaxEqCountCmd or AdaptiveEqCmd => AnalyzeSingleValue(cmd, p),
            _ => AnalyzeGeneric(cmd, p),
        };
    }

    /// <summary>EQ 全表命令的判定与双格式解读。</summary>
    private static ProbeVerdict AnalyzeEqTable(byte cmd, byte[] p)
    {
        string cmdName = EqFamilyName(cmd);
        if (p.Length < EqTableMinParamsLength)
        {
            return ProbeVerdict.Miss(
                $"回声一致但数据不足（{p.Length}B < {EqTableMinParamsLength}B）",
                $"declaredLen={(p.Length >= 2 ? p[1] : 0)}，不足以构成 10 段 EQ 表：{Hex(p)}");
        }

        string interpretation = DescribeEqTable(p);
        return ProbeVerdict.Pass(
            $"命中（{cmdName} 全表：params({p.Length}B) ≥ {EqTableMinParamsLength}B）",
            interpretation);
    }

    /// <summary>0x44（自定义 EQ 槽位数）/ 0x27（自适应 EQ 状态）类单值读的判定。</summary>
    private static ProbeVerdict AnalyzeSingleValue(byte cmd, byte[] p)
    {
        string name = EqFamilyName(cmd);
        if (p.Length < 2)
        {
            return ProbeVerdict.Miss("回声一致但缺声明长度字节", Hex(p));
        }

        int declared = p[1];
        if (declared <= 0 || p.Length < 2 + declared)
        {
            return ProbeVerdict.Miss(
                $"{name} 声明长度 {declared} 为 0 或数据被截断",
                $"params({p.Length}B) = {Hex(p)}");
        }

        string detail = cmd == MaxEqCountCmd
            ? $"自定义 EQ 槽位数 = {p[2]}（声明 {declared}B，值区 {Hex(p.AsSpan(2, declared))}）"
            : $"自适应 EQ 状态 = 0x{p[2]:X2}（1=关/2=开 为 Quicky 约定；声明 {declared}B，值区 {Hex(p.AsSpan(2, declared))}）";
        return ProbeVerdict.Pass($"命中（{name} 有数据）", detail);
    }

    /// <summary>其它 command（如 0x2F 电量 / 0x30 版本）的通用判定。</summary>
    private static ProbeVerdict AnalyzeGeneric(byte cmd, byte[] p)
    {
        if (p.Length < 2 || p[1] <= 0 || p.Length < 2 + p[1])
        {
            return ProbeVerdict.Miss(
                "有回声但无数据（声明长度为 0 或被截断）",
                $"params({p.Length}B) = {Hex(p)}");
        }
        return ProbeVerdict.Pass(
            $"命中（command 0x{cmd:X2} 可读，数据 {p[1]}B）",
            $"值区 = {Hex(p.AsSpan(2, p[1]))}（[cmd][len][value...] 布局）");
    }

    /// <summary>EQ 家族 command 的中文名。</summary>
    public static string EqFamilyName(byte cmd) => cmd switch
    {
        EqV1Cmd => "EQ v1(0x20)",
        EqV2Cmd => "EQ v2(0x22)",
        AdaptiveEqCmd => "自适应 EQ(0x27)",
        MaxEqCountCmd => "EQ 槽位数(0x44)",
        EqLeftCmd => "左通道 EQ(0x46)",
        EqRightCmd => "右通道 EQ(0x47)",
        _ => $"command 0x{cmd:X2}",
    };

    /// <summary>
    /// 解读 EQ 全表 params：先试 Python/H3 格式 <c>[cmd][len][preset][70 FE][10×7B(freq,gain,refGain,Q)]</c>，
    /// 再试 Quicky 格式 <c>[cmd][len][eqType][masterGain 2B][N×7B(freq,gain,Q,bandType)]</c>；
    /// 两者都不匹配则打印原样字节。至少能解出 7 段才算成功解析。
    /// </summary>
    public static string DescribeEqTable(byte[] p)
    {
        // Python/H3 格式：数据区从 offset 2 开始，[preset][70][FE] + 7B×N
        if (p.Length >= 5 + 7 && p[3] == 0x70 && p[4] == 0xFE)
        {
            int bands = (p.Length - 5) / 7;
            var gains = FormatBandGains(p, 5, bands, quicky: false);
            byte preset = p[2];
            string presetName = preset < EqPresets.Names.Length ? EqPresets.Names[preset] : "?";
            return $"Python/H3 格式：预设 0x{preset:X2}（{presetName}），声明长度={p[1]}，段数={bands}；增益(1/100dB)：{gains}";
        }

        // Quicky 格式：数据区从 offset 2 开始，[eqType][masterGainLo][masterGainHi] + 7B×N
        if (p.Length >= 5)
        {
            int bands = (p.Length - 5) / 7;
            if (bands >= 7)
            {
                short masterGain = (short)(p[3] | (p[4] << 8));
                var gains = FormatBandGains(p, 5, bands, quicky: true);
                return $"Quicky 格式：eqType=0x{p[2]:X2}，masterGain={masterGain}（1/100dB），声明长度={p[1]}，段数={bands}；增益(1/100dB)：{gains}";
            }
        }

        return $"两种已知格式（Python preset+70FE / Quicky eqType+masterGain）都不匹配，params({p.Length}B) = {Hex(p)}";
    }

    /// <summary>从 <paramref name="start"/> 起按 7B/段提取增益，拼接为紧凑文本。</summary>
    private static string FormatBandGains(byte[] p, int start, int bands, bool quicky)
    {
        var parts = new List<string>(bands);
        for (int i = 0; i < bands && start + (i + 1) * 7 <= p.Length; i++)
        {
            int o = start + i * 7;
            ushort freq = (ushort)(p[o] | (p[o + 1] << 8));
            short gain = (short)(p[o + 2] | (p[o + 3] << 8));
            string band = EqBands.Frequencies.Contains(freq) ? $"{freq}Hz" : $"0x{freq:X4}";
            parts.Add(quicky ? $"{band}={gain}(Q{p[o + 4]},bt0x{p[o + 6]:X2})" : $"{band}={gain}");
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 解读 GET_TARGET_FEATURE_MAP（opCode 2）回包：位图按字节序逐位置位列出
    /// （bit N = 第 N/8 字节的第 N%8 位，LSB 在前），同时给出整包十六进制。
    /// 杰理 SDK 未公开各 bit 含义，本方法只报事实不做断言。
    /// </summary>
    public static string DescribeFeatureMap(byte[] p)
    {
        if (p.Length == 0)
        {
            return "载荷为空——固件未声明任何 feature 位。";
        }

        var bits = new List<int>();
        for (int i = 0; i < p.Length; i++)
        {
            for (int b = 0; b < 8; b++)
            {
                if ((p[i] & (1 << b)) != 0)
                {
                    bits.Add(i * 8 + b);
                }
            }
        }

        return $"feature 位图（{p.Length}B）：{Hex(p)}；置位 bit：{string.Join(' ', bits)}（bit N = 第 N/8 字节第 N%8 位，LSB 在前；各 bit 含义杰理未公开，需对照实测能力）";
    }

    /// <summary>
    /// 解读 GET_SYS_INFO（opCode 7）回包：首字节为 function 回声；其后数据按两种布局
    /// 同时尝试——杰理系统属性 <c>[attrId][len][value...]</c> 与 RCSP TLV <c>[L][T][V]</c>，
    /// 都打印出来供比对（第一轮掩码 0x001810 只回了 1 字节 07，即纯 function 回声）。
    /// </summary>
    public static string DescribeSysInfoPayload(byte[] p)
    {
        if (p.Length == 0)
        {
            return "载荷为空。";
        }
        if (p.Length == 1)
        {
            return $"仅 function 回声 0x{p[0]:X2}（无任何属性——与本机第一轮掩码 0x001810 的表现相同）。";
        }

        var text = new StringBuilder($"function 回声 0x{p[0]:X2}，其余 {p.Length - 1}B = {Hex(p.AsSpan(1))}；");
        string attrs = TryParseAttrIdLen(p.AsSpan(1));
        text.Append("属性表解读: ").Append(attrs);
        text.Append("；TLV 解读: ").Append(DescribeTlv(p.AsSpan(1)));
        return text.ToString();
    }

    /// <summary>按 <c>[attrId][len][value...]</c> 布局尽力解析，异常布局返回提示文本（不抛异常）。</summary>
    private static string TryParseAttrIdLen(ReadOnlySpan<byte> data)
    {
        var parts = new List<string>();
        int i = 0;
        while (i + 2 <= data.Length)
        {
            byte id = data[i];
            int len = data[i + 1];
            if (len == 0 || i + 2 + len > data.Length)
            {
                parts.Add($"（0x{id:X2},len={len} 越界，解析停止）");
                break;
            }
            parts.Add($"attr 0x{id:X2} = {Hex(data.Slice(i + 2, len))}");
            i += 2 + len;
        }
        return parts.Count > 0 ? string.Join(' ', parts) : "无（数据不构成 [attrId][len][value] 序列）";
    }

    /// <summary>按 RCSP TLV <c>[L][T][V]</c> 布局尽力解析（复用 <see cref="TlvParser"/>）。</summary>
    private static string DescribeTlv(ReadOnlySpan<byte> data)
    {
        var entries = TlvParser.Parse(data);
        if (entries.Count == 0)
        {
            return "无合法 TLV 项";
        }
        var parts = entries.Select(e => $"type=0x{e.Type:X2} len={e.Value.Length + 1} value={Hex(e.Value)}");
        return string.Join(' ', parts);
    }

    /// <summary>GATT 直读特征 000B 的字节按 Quicky 直写格式 <c>[eqType][masterGain 2B][7B×N]</c> 解读（无帧、无 [cmd][len] 头）。</summary>
    public static string DescribeDirectEqBytes(byte[] data)
    {
        if (data.Length < 3)
        {
            return $"不足 3B，无法按 [eqType][masterGain][段数据] 解读：{Hex(data)}";
        }

        int bands = (data.Length - 3) / 7;
        short masterGain = (short)(data[1] | (data[2] << 8));
        if (bands >= 7)
        {
            var gains = FormatBandGains(data, 3, bands, quicky: true);
            return $"Quicky 直读格式：eqType=0x{data[0]:X2}，masterGain={masterGain}，段数={bands}；增益(1/100dB)：{gains}";
        }
        return $"eqType=0x{data[0]:X2}，masterGain={masterGain}，剩余 {data.Length - 3}B 不足 7 段：{Hex(data)}";
    }

    // ───────────────────────── 阶段 4a：扩展功能探针解读（纯函数） ─────────────────────────

    /// <summary>
    /// 阶段 4a 调研到的非 EQ CUSTOM 命令中文名（出处：Quicky docs/protocol.md 命令总表、
    /// qcy-h3s-windows h3s_device.py 与 docs/PROTOCOLO-LEITURAS.md 的 APK ConstantCMDID 对照）。
    /// </summary>
    public static string CustomCmdName(byte cmd) => cmd switch
    {
        PowerManager.Cmd => "自动关机/电源管理(0x14)",
        0x19 => "语音名称(0x19)",
        0x1D => "提示音音量(0x1D)",
        0x24 => "双设备连接(0x24, CMDID_1T2)",
        0x2B => "按键功能旧通道(0x2B)",
        0x3D => "播放提示音(0x3D, 写)",
        _ => $"command 0x{cmd:X2}",
    };

    /// <summary>
    /// 解读提示音音量读响应（<c>[0x1D][声明长度][volume][max?]</c>）。出处：Quicky
    /// ParseToneVolume（第 1 字节音量、可选第 2 字节最大值，缺省 100）与 qcy-h3s 真机实测
    /// <c>1d 02 06 08</c>（H3S 家族音量 0..8、第 4 字节为最大值）。null = 回声不符或无数据。
    /// </summary>
    public static string? DescribeToneVolume(byte[] p)
    {
        if (p.Length < 3 || p[0] != 0x1D || p[1] < 1)
        {
            return null;
        }

        int volume = p[2];
        int max = p.Length >= 4 ? p[3] : 100;
        return $"提示音音量 = {volume}（最大 {max}；Quicky 约定缺省 100，H3S 家族实测范围 0..8）";
    }

    /// <summary>
    /// 解读语音名称读响应（<c>[0x19][声明长度][UTF-8/ASCII 字节 × N]</c>）。出处：Quicky
    /// dispatch.go（rename/audio lang 按 NUL 结尾字符串解析）与 qcy-h3s 真机实测
    /// <c>19 04 4a 4c 30 30</c> = "JL00"。null = 回声不符或无数据。
    /// </summary>
    public static string? DescribeVoiceName(byte[] p)
    {
        if (p.Length < 2 || p[0] != 0x19 || p[1] < 1)
        {
            return null;
        }

        int declared = Math.Min(p[1], p.Length - 2);
        string text = System.Text.Encoding.UTF8.GetString(p, 2, declared).TrimEnd('\0');
        return $"语音名称 = \"{text}\"（{declared}B UTF-8，NUL 截断）";
    }

    /// <summary>
    /// 解读单字节开关类读响应（<c>[cmd][01][value]</c>，双设备连接 0x24 等）。
    /// 出处：qcy-h3s 真机实测 <c>24 01 00</c>（CMDID_1T2）；value 的开/关极性参考仓库均未验证，
    /// 只报原始字节不做断言。null = 回声不符或声明长度为 0。
    /// </summary>
    public static string? DescribeSwitchRead(byte cmd, byte[] p)
    {
        if (p.Length < 3 || p[0] != cmd || p[1] < 1)
        {
            return null;
        }

        return $"{CustomCmdName(cmd)} 值 = 0x{p[2]:X2}（声明 {p[1]}B；开/关极性参考仓库未验证，仅报原始值）";
    }

    /// <summary>大写十六进制、空格分隔。</summary>
    private static string Hex(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
