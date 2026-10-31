namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// 播放提示音 / 查找耳机命令编码（CUSTOM 通道，R2 级——外部音效、无破坏性，
/// 风险分级出处 qcy-h3s <c>docs/COMANDOS-DESTRUTIVOS.md</c>）。
/// <list type="bullet">
/// <item>播放提示音：CUSTOM params <c>3D 01 &lt;toneID&gt;</c> —— 字节出处
/// Quicky <c>internal/command/TonePlay.go</c>（0x3D + 长度 0x01 + toneID）、
/// qcy-h3s-windows <c>h3s_device.py</c> play_tone（外部效果：按当前音量外放）。
/// 真机 H3：ack 通道正常（status=0），音效未触发验证——UI 首次使用将验证效果。</item>
/// <item>查找耳机：CUSTOM params <c>4B 01 01</c> —— 字节出处
/// qcy-h3s-windows <c>h3s_device.py</c> find_earphones（响铃定位；外部效果：当前音量外放）。
/// 真机 H3：ack 通道正常，效果未触发验证——UI 首次使用将验证效果。</item>
/// </list>
/// 两命令均为「写 + ack 判定」（Request Data 读回声为空载荷，无回读通道），
/// 成功判定 = <see cref="H3Client.SendAsync"/> 返回应答包（status=0）。
/// </summary>
public static class ToneCommands
{
    /// <summary>播放提示音 command ID（CUSTOM params = 3D 01 &lt;toneID&gt;）。</summary>
    public const byte PlayToneCmd = 0x3D;

    /// <summary>查找耳机 command ID（CUSTOM params = 4B 01 01）。</summary>
    public const byte FindEarbudsCmd = 0x4B;

    /// <summary>两命令的数据区长度字节（= 1，后跟 1 字节参数）。</summary>
    public const byte PayloadLength = 0x01;

    /// <summary>查找耳机固定参数（0x01 = 开启响铃定位）。</summary>
    public const byte FindEarbudsArg = 0x01;

    /// <summary>构建播放提示音写载荷：<c>3D 01 &lt;toneID&gt;</c>。</summary>
    public static byte[] BuildPlayTonePayload(byte toneId) => [PlayToneCmd, PayloadLength, toneId];

    /// <summary>构建查找耳机写载荷：<c>4B 01 01</c>。</summary>
    public static byte[] BuildFindEarbudsPayload() => [FindEarbudsCmd, PayloadLength, FindEarbudsArg];
}
