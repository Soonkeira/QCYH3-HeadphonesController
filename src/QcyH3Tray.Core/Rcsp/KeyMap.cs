namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// 一条按键映射（GET_ADV_INFO type=2 按键表的三元组）。
/// </summary>
/// <param name="KeyNumber">物理键编号（真机 H3 出现 07/03/05/09、H3S 实测 3/5/7；语义未公开）。</param>
/// <param name="Action">动作索引 1..5（单击/双击/三击/四击/长按，见 <see cref="KeyMap.ActionName"/>）。</param>
/// <param name="Function">功能 ID（见 <see cref="KeyMap.FunctionName"/>；0x00/0x7F=无功能，0xFF=切换降噪模式）。</param>
public sealed record KeyMappingEntry(byte KeyNumber, byte Action, byte Function);

/// <summary>
/// 按键映射编解码（KeyMapCodec，GET_ADV_INFO type=2 / SET_ADV_INFO type=2），出处于
/// qcy-h3s-windows 对 H3S 真机与官方 APK 反编译的交叉验证：
/// <list type="bullet">
/// <item>读：GET_ADV_INFO（0xC1）掩码 <c>1 &lt;&lt; 2</c>（<see cref="AdvType.KeySettings"/>）→
/// TLV <c>[L][0x02][keyNum action function × N]</c>，每条 3 字节。
/// qcy-h3s-windows <c>h3s_device.py</c> get_key_settings 与 <c>docs/BOTOES.md</c>
/// （H3S 实测 <c>2e 02</c> + 15 三元组；本机 H3 全掩码 153B TLV 中 type=02 同构）。</item>
/// <item>写：SET_ADV_INFO（0xC0）LTV <c>[len = 1 + 3N][0x02][三元组 × N]</c> ——
/// APK <c>BtRcspOpImpl.configKeySettings()</c> → <c>packLTVPacket(2, dados)</c> →
/// <c>modifyAdvInfo(2, …)</c>（BOTOES.md 记载并在 H3S 真机验证：写回原表 → 回读逐字节一致）。
/// <b>整表写入</b>：SDK 把载荷当作完整配置，部分列表会清掉其余按键。</item>
/// <item>陷阱：CUSTOM 0x2B（CMDID_BUTTONFUNC）读超时、写被静默忽略（BOTOES.md 实测），
/// APK 也不用它——按键读写必须走 ADV_INFO type=2。</item>
/// </list>
/// </summary>
public static class KeyMap
{
    /// <summary>ADV_INFO 属性类型：按键映射表（rcsp.py AdvType.KEY_SETTINGS）。</summary>
    public const byte AdvInfoType = AdvType.KeySettings;

    /// <summary>每条映射的线上字节数：[keyNum][action][function]。</summary>
    public const int BytesPerEntry = 3;

    /// <summary>
    /// 解析 GET_ADV_INFO type=2 的 TLV 值（不含 [L][T] 头）为三元组列表。
    /// 与 h3s get_key_settings 相同的容错：末尾不足 3 字节的残尾忽略；
    /// 连一条完整三元组都没有时返回 <see langword="null"/>。
    /// </summary>
    public static List<KeyMappingEntry>? ParseAdvInfoValue(ReadOnlySpan<byte> value)
    {
        if (value.Length < BytesPerEntry)
        {
            return null;
        }

        var entries = new List<KeyMappingEntry>(value.Length / BytesPerEntry);
        for (int i = 0; i + BytesPerEntry <= value.Length; i += BytesPerEntry)
        {
            entries.Add(new KeyMappingEntry(value[i], value[i + 1], value[i + 2]));
        }
        return entries;
    }

    /// <inheritdoc cref="ParseAdvInfoValue(ReadOnlySpan{byte})"/>
    public static List<KeyMappingEntry>? ParseAdvInfoValue(byte[] value) => ParseAdvInfoValue(value.AsSpan());

    /// <summary>
    /// 构建 SET_ADV_INFO（0xC0）完整 LTV 载荷：<c>[len = 1 + 3N][0x02][三元组 × N]</c>。
    /// 调用方必须传整表（h3s set_key_settings 的守卫：空表与超 1 字节长度都拒绝）。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="entries"/> 为空或总长超出 1 字节 LTV。</exception>
    public static byte[] BuildSetAdvInfoPayload(IReadOnlyList<KeyMappingEntry> entries)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("按键映射写入至少需要 1 条三元组（SDK 按整表处理，禁止空写）。", nameof(entries));
        }
        long ltvLength = 1L + entries.Count * BytesPerEntry;
        if (ltvLength > 0xFF)
        {
            throw new ArgumentException($"按键映射过大（{entries.Count} 条超出 1 字节 LTV 长度）。", nameof(entries));
        }

        var payload = new byte[2 + entries.Count * BytesPerEntry];
        payload[0] = (byte)ltvLength;
        payload[1] = AdvInfoType;
        for (int i = 0; i < entries.Count; i++)
        {
            payload[2 + i * BytesPerEntry] = entries[i].KeyNumber;
            payload[3 + i * BytesPerEntry] = entries[i].Action;
            payload[4 + i * BytesPerEntry] = entries[i].Function;
        }
        return payload;
    }

    /// <summary>
    /// 用「读到的 type=2 原始值」构建逐字节一致的回写 LTV（探针原样回写用：
    /// 即使出现非 3 倍数的残尾也原样带回，保证写回 == 读到）。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rawValue"/> 为空或 LTV 长度超出 1 字节。</exception>
    public static byte[] BuildSetAdvInfoPayloadRaw(ReadOnlySpan<byte> rawValue)
    {
        if (rawValue.IsEmpty)
        {
            throw new ArgumentException("按键映射原始值不能为空。", nameof(rawValue));
        }
        long ltvLength = 1L + rawValue.Length;
        if (ltvLength > 0xFF)
        {
            throw new ArgumentException($"按键映射原始值过大（{rawValue.Length}B 超出 1 字节 LTV 长度）。", nameof(rawValue));
        }

        var payload = new byte[2 + rawValue.Length];
        payload[0] = (byte)ltvLength;
        payload[1] = AdvInfoType;
        rawValue.CopyTo(payload.AsSpan(2));
        return payload;
    }

    /// <summary>动作索引语义（出处：Quicky KeyID 家族 单击/双击/三击/四击/长按 + BOTOES.md 实测 action 1..5；4=四击为同族推断）。</summary>
    public static string ActionName(byte action) => action switch
    {
        1 => "单击",
        2 => "双击",
        3 => "三击",
        4 => "四击",
        5 => "长按",
        _ => $"未知(0x{action:X2})",
    };

    /// <summary>
    /// 功能 ID 语义（出处：Quicky <c>internal/command/KeyFunction.go</c> FuncID 表 +
    /// BOTOES.md 实测 0x00/0x7F=无功能、0xFF=切换降噪模式（APK KEY_FUNC_ID_SWITCH_ANC_MODE））。
    /// </summary>
    public static string FunctionName(byte function) => function switch
    {
        0x00 => "无功能",
        0x01 => "播放/暂停",
        0x02 => "上一曲",
        0x03 => "下一曲",
        0x04 => "语音助手",
        0x05 => "音量+",
        0x06 => "音量-",
        0x07 => "游戏模式切换",
        0x08 => "接听",
        0x09 => "拒接",
        0x0A => "通话保持",
        0x0B => "重拨",
        0x7F => "无功能（APK 127→0 归一化）",
        0xFF => "切换降噪模式",
        _ => $"未知(0x{function:X2})",
    };

    /// <summary>按键表的人类可读多行描述（探针输出用；调用方逐行打印）。</summary>
    public static IReadOnlyList<string> DescribeEntries(IReadOnlyList<KeyMappingEntry> entries)
    {
        var lines = new List<string>(entries.Count);
        foreach (KeyMappingEntry entry in entries)
        {
            lines.Add(
                $"keyNum=0x{entry.KeyNumber:X2}（语义未知，物理键编号） action={entry.Action}（{ActionName(entry.Action)}） " +
                $"function=0x{entry.Function:X2}（{FunctionName(entry.Function)}）");
        }
        return lines;
    }
}
