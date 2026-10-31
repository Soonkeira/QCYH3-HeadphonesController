using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Cli;

// ───────────────────────── 阶段 4a：扩展功能探针（probe-off / probe-keys / probe-tone / probe-multi） ─────────────────────────
// 调研出处（写命令全部有参考仓库源码背书，读命令只读无害）：
//  - 自动关机 0x14：读 FE 01 14 → [14][04][powerOff u16LE][current u16LE]（Quicky response.go ParsePowerManager；
//    qcy-h3s docs/PROTOCOLO-LEITURAS.md 真机 14 04 ff ff 00 00，与本机一致）。写 14 04 [lo][hi][00 00]
//    （Quicky command/BookingReboot.go、docs/protocol.md「0x14 — Power Manager」、qcy-h3s set_power_manager）。
//  - 按键映射：读 GET_ADV_INFO 掩码 1<<2 → TLV type=02 三元组 [keyNum action function]×N；写 SET_ADV_INFO(0xC0)
//    LTV [len=1+3N][02][三元组]（qcy-h3s h3s_device.py set_key_settings + docs/BOTOES.md 真机整表回写验证）。
//    CUSTOM 0x2B 旧通道读超时、写被忽略（BOTOES.md），探针中仅作对照。
//  - 提示音：读 0x1D（音量+最大值，Quicky ParseToneVolume / qcy-h3s 1d 02 06 08）、0x19（语音名称，19 04 4a 4c 30 30）。
//    写 3D 01 <toneID>（播放提示音）、4B 01 01（查找耳机响铃）——仅列出字节不接线，待主智能体决策真机触发。
//  - 双设备连接：读 0x24（CMDID_1T2，qcy-h3s 真机 24 01 00）；写命令在参考仓库均无出处，不实现。

/// <summary>探针命令的读写窗口与共享会话（见上方调研出处注释）。</summary>
internal static partial class Program
{
    private static readonly TimeSpan ProbeReadWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbeWriteWindow = TimeSpan.FromSeconds(2);

    /// <summary>探针共享会话：连接 + 握手完成后的抓包通道与协议客户端（Dispose 逆序释放）。</summary>
    private sealed record ProbeSession(DebugCapture Capture, BleTransport Transport, H3Client Client) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Transport.Dispose();
        }
    }

    /// <summary>解析目标 → 连接 → 握手；任一步失败时打印错误并返回 null（调用方直接退出）。</summary>
    private static async Task<ProbeSession?> OpenProbeSessionAsync(string targetArgument, string title)
    {
        (ulong Address, string Name)? target = await ResolveTargetAsync(targetArgument).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }
        (ulong address, string name) = target.Value;

        var capture = new DebugCapture();
        var transport = new BleTransport();
        transport.DataReceived += capture.OnRawChunk;   // 先订原始块：每块先打印、再进协议解析
        var client = new H3Client(transport);
        client.PacketTraced += capture.OnFrameTraced;

        Console.WriteLine($"── {title}：{name}（{BleScanResult.FormatAddress(address)}）──");
        try
        {
            await transport.ConnectAsync(address).WaitAsync(ConnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"连接超时（{ConnectTimeout.TotalSeconds:0} 秒）：设备可能已关机、入盒或不广播。");
            transport.Dispose();
            return null;
        }
        capture.Log("GATT 已连接（服务 A002，写特征 0001，通知特征 0002）");

        Packet? handshake = await client.HandshakeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (handshake is null)
        {
            capture.Log("握手（NOTIFY_COMMUNICATION_WAY 00 00）2 秒无响应——无法继续探测，请确认设备未被其它 App 占用。");
            transport.Dispose();
            return null;
        }
        capture.Log($"握手成功: {DescribePacket(handshake)}");
        return new ProbeSession(capture, transport, client);
    }

    /// <summary>发 CUSTOM 读（FE 01 cmd）并等满接收窗口，打印判定与解读（探针通用步骤）。</summary>
    private static async Task<Packet?> ProbeCustomReadAsync(ProbeSession session, string label, byte cmdId)
    {
        session.Capture.BeginPhase(0);
        session.Capture.Log($"── {label}（FE 01 {cmdId:X2} = {RcspProbe.CustomCmdName(cmdId)}，窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        var request = new Packet(OpCode.Custom, parameters: [0xFE, 0x01, cmdId]);
        Packet? response = await SendAndListenAsync(session.Client, session.Capture, label, request, ProbeReadWindow).ConfigureAwait(false);
        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead(request.Params, response);
        session.Capture.Log($"判定: {verdict.Judgment}");
        session.Capture.Log($"解读: {verdict.Interpretation}");
        return response;
    }

    // ───────────────────────── probe-off（自动关机读写验证） ─────────────────────────

    /// <summary>
    /// 自动关机（cmd 0x14）读写验证：读基线 → 打印当前值解读 → 当前值原样回写 → 回读对比。
    /// 唯一写操作为「基线原样回写」（把读到的 [时长][计时] 逐字节写回），耳机最终状态与实验前一致。
    /// </summary>
    private static async Task<int> CmdProbeOffAsync(string targetArgument)
    {
        using ProbeSession? session = await OpenProbeSessionAsync(targetArgument, "probe-off 自动关机读写验证").ConfigureAwait(false);
        if (session is null)
        {
            return ExitError;
        }
        DebugCapture capture = session.Capture;
        capture.Log("安全声明：唯一写操作为步骤 2 的「当前值原样回写」（14 04 + 读到的时长/计时逐字节写回），不改变任何设置。");

        // 步骤 1：读基线
        capture.BeginPhase(0);
        capture.Log($"── 步骤 1：读自动关机基线（窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        PowerManagerInfo? baseline = await ReadPowerManagerAsync(session).ConfigureAwait(false);
        if (baseline is null)
        {
            capture.Log("结论: 基线读取失败（无响应 / 回声非 0x14 / 数据不足 4B）——未执行任何写操作，耳机状态未改变。");
            return ExitError;
        }
        capture.Log($"基线解读: {PowerManager.Describe(baseline)}");
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 2：当前值原样回写（BuildWritePayload(基线) 与读响应逐字节一致，已由单测证明）
        byte[] writePayload = PowerManager.BuildWritePayload(baseline.PowerOffMinutes, baseline.CurrentMinutes);
        capture.BeginPhase(0);
        capture.Log($"── 步骤 2：当前值原样回写（载荷 {writePayload.Length}B: {Hex(writePayload)}）──");
        Task<Packet?> writeTask = StartTracedSend(session.Client, capture, "自动关机·原样回写",
            new Packet(OpCode.Custom, parameters: writePayload), ProbeWriteWindow);
        await Task.Delay(EqProbeStepGap).ConfigureAwait(false);
        Packet? writeAck = await writeTask.ConfigureAwait(false);
        capture.Log($"写入结果: {(writeAck is null ? "无 ack（是否生效由步骤 3 读回判定）" : DescribePacket(writeAck))}");
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 3：回读对比
        capture.BeginPhase(0);
        capture.Log($"── 步骤 3：回读对比（窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        PowerManagerInfo? after = await ReadPowerManagerAsync(session).ConfigureAwait(false);
        if (after is null)
        {
            capture.Log("回读失败（无响应/不合法）——无法读回验证，但步骤 2 写入的就是基线值本身，耳机状态最多仍为原值。");
        }
        else if (after == baseline)
        {
            capture.Log("结论: 回读与基线一致 —— 0x14 读→写→读通道打通（原样回写已确认），耳机保持原设置。");
        }
        else
        {
            capture.Log($"结论: 回读为 {PowerManager.Describe(after)}，与基线不一致 —— 计时字段在走或固件自行改写，请对照原始帧分析。");
        }

        LogLeftoverAndProbeDone(capture, session.Client, "probe-off");
        return ExitOk;
    }

    /// <summary>发 FE 01 14 读、等满接收窗口并按电源管理格式解析（与 H3Client.GetPowerManagerAsync 同参同解析）。</summary>
    private static async Task<PowerManagerInfo?> ReadPowerManagerAsync(ProbeSession session)
    {
        var request = new Packet(OpCode.Custom, parameters: [0xFE, 0x01, PowerManager.Cmd]);
        Packet? response = await SendAndListenAsync(session.Client, session.Capture, "自动关机读", request, ProbeReadWindow).ConfigureAwait(false);
        PowerManagerInfo? info = response is null ? null : PowerManager.ParseResponse(response.Params);
        session.Capture.Log(info is null
            ? "解析: 未拿到合法 0x14 表（无响应 / 回声不符 / 数据不足 4B）"
            : $"解析: {PowerManager.Describe(info)}");
        return info;
    }

    // ───────────────────────── probe-keys（按键映射读写验证） ─────────────────────────

    /// <summary>GET_ADV_INFO type=02 按键表的一次读取结果：解析出的三元组 + TLV 原始值（回写用）。</summary>
    private sealed record KeyMapReadResult(List<KeyMappingEntry> Entries, byte[] RawValue);

    /// <summary>
    /// 按键映射读写验证：GET_ADV_INFO 掩码 1&lt;&lt;2 读键表（type=02 三元组）→ 0x2B 旧通道对照 →
    /// SET_ADV_INFO 整表原样回写 → 再读对比。唯一写操作为「当前键表原样回写」（LTV 逐字节还原），不改任何映射。
    /// </summary>
    private static async Task<int> CmdProbeKeysAsync(string targetArgument)
    {
        using ProbeSession? session = await OpenProbeSessionAsync(targetArgument, "probe-keys 按键映射读写验证").ConfigureAwait(false);
        if (session is null)
        {
            return ExitError;
        }
        DebugCapture capture = session.Capture;
        capture.Log("安全声明：唯一写操作为步骤 3 的「当前键表整表原样回写」（SET_ADV_INFO LTV type=02），不改变任何按键映射。");

        // 步骤 1：GET_ADV_INFO 掩码 1<<2 读按键表
        KeyMapReadResult? baseline = await ReadKeyMapAsync(session, "步骤 1").ConfigureAwait(false);
        if (baseline is null)
        {
            capture.Log("结论: 按键表读取失败（无响应 / 无 type=02 TLV / 无完整三元组）——未执行任何写操作，耳机状态未改变。");
            return ExitError;
        }
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 2：0x2B 旧通道对照（BOTOES.md 实测读超时——预期本机同样无数据，仅作交叉验证）
        await ProbeCustomReadAsync(session, "步骤 2：0x2B 旧通道对照", 0x2B).ConfigureAwait(false);
        capture.Log("对照说明: APK 反编译证实 0x2B 不用于按键映射（读超时、写被忽略），读写必须走 ADV_INFO type=02。");
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 3：SET_ADV_INFO 整表原样回写（BuildSetAdvInfoPayloadRaw 保证与读到的 TLV 值逐字节一致）
        byte[] writePayload = KeyMap.BuildSetAdvInfoPayloadRaw(baseline.RawValue);
        capture.BeginPhase(0);
        capture.Log($"── 步骤 3：按键表整表原样回写（SET_ADV_INFO 0xC0，载荷 {writePayload.Length}B: {Hex(writePayload)}）──");
        Task<Packet?> writeTask = StartTracedSend(session.Client, capture, "按键表·整表原样回写",
            new Packet(OpCode.SetAdvInfo, parameters: writePayload), ProbeWriteWindow);
        await Task.Delay(EqProbeStepGap).ConfigureAwait(false);
        Packet? writeAck = await writeTask.ConfigureAwait(false);
        capture.Log($"写入结果: {(writeAck is null ? "无 ack（是否生效由步骤 4 读回判定）" : DescribePacket(writeAck))}");
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 4：再读对比
        capture.BeginPhase(0);
        capture.Log($"── 步骤 4：回读对比（窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        KeyMapReadResult? after = await ReadKeyMapAsync(session, "步骤 4").ConfigureAwait(false);
        if (after is null)
        {
            capture.Log("回读失败（无响应/无 type=02）——无法读回验证，但步骤 3 写入的就是基线表本身，映射最多仍为原值。");
        }
        else if (after.RawValue.AsSpan().SequenceEqual(baseline.RawValue))
        {
            capture.Log($"结论: 回读键表与基线逐字节一致（{after.Entries.Count} 条三元组）—— GET_ADV_INFO type=02 读、" +
                        "SET_ADV_INFO type=02 写通道打通（整表回写已确认），耳机保持原映射。");
        }
        else
        {
            capture.Log("结论: 回读键表与基线不一致 —— 请对照两次原始帧逐字节分析（固件可能自行改写或顺序不同）。");
        }

        LogLeftoverAndProbeDone(capture, session.Client, "probe-keys");
        return ExitOk;
    }

    /// <summary>GET_ADV_INFO 掩码 1&lt;&lt;2 读按键表：打印三元组语义并返回（三元组列表, TLV 原始值）；失败返回 null。</summary>
    private static async Task<KeyMapReadResult?> ReadKeyMapAsync(ProbeSession session, string stepLabel)
    {
        DebugCapture capture = session.Capture;
        capture.BeginPhase(0);
        capture.Log($"── {stepLabel}：读按键表（GET_ADV_INFO 掩码 00 00 00 {(1u << AdvType.KeySettings):X2}，窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        var request = new Packet(OpCode.GetAdvInfo, parameters: [0x00, 0x00, 0x00, (byte)(1u << AdvType.KeySettings)]);
        Packet? response = await SendAndListenAsync(session.Client, capture, "按键表读", request, ProbeReadWindow).ConfigureAwait(false);

        if (response is not null)
        {
            capture.Log($"TLV 概览: {DescribeTlvInline(response.Params)}");
        }
        List<TlvEntry> tlvEntries = response is null ? [] : TlvParser.Parse(response.Params);
        foreach (TlvEntry entry in tlvEntries)
        {
            if (entry.Type != AdvType.KeySettings)
            {
                continue;
            }
            List<KeyMappingEntry>? entries = KeyMap.ParseAdvInfoValue(entry.Value);
            if (entries is null)
            {
                capture.Log($"解析: type=02 值区 {entry.Value.Length}B 不足一条完整三元组");
                continue;
            }
            capture.Log($"解析: {entries.Count} 条映射（值区 {entry.Value.Length}B）:");
            foreach (string line in KeyMap.DescribeEntries(entries))
            {
                capture.Log($"  {line}");
            }
            return new KeyMapReadResult(entries, entry.Value);
        }

        capture.Log("解析: 响应中无 type=02（KEY_SETTINGS）TLV —— 本固件不按此掩码回报按键表。");
        return null;
    }

    // ───────────────────────── probe-tone（提示音只读探测） ─────────────────────────

    /// <summary>
    /// 提示音只读探测（无写操作）：提示音音量（0x1D 读）→ 语音名称（0x19 读）→ 播放命令读回声（0x3D），
    /// 最后列出已调研到的写命令字节（0x3D 播放 / 0x4B 查找耳机）——只列出不发送，待主智能体决策真机触发。
    /// </summary>
    private static async Task<int> CmdProbeToneAsync(string targetArgument)
    {
        using ProbeSession? session = await OpenProbeSessionAsync(targetArgument, "probe-tone 提示音只读探测").ConfigureAwait(false);
        if (session is null)
        {
            return ExitError;
        }
        DebugCapture capture = session.Capture;
        capture.Log("安全声明：本命令为纯读探测（0x1D/0x19/0x3D 的 Request Data 读），不含任何写操作；播放/查找类写命令只列出字节，不发送。");

        // 步骤 1：提示音音量（0x1D）
        capture.BeginPhase(0);
        capture.Log($"── 步骤 1：提示音音量读（窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        Packet? toneVolume = await SendAndListenAsync(session.Client, capture, "提示音音量读",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x1D]), ProbeReadWindow).ConfigureAwait(false);
        LogSpecializedOrGeneric(capture, 0x1D, toneVolume, RcspProbe.DescribeToneVolume(toneVolume?.Params ?? []));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 2：语音名称（0x19）
        capture.BeginPhase(0);
        capture.Log($"── 步骤 2：语音名称读（窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        Packet? voiceName = await SendAndListenAsync(session.Client, capture, "语音名称读",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x19]), ProbeReadWindow).ConfigureAwait(false);
        LogSpecializedOrGeneric(capture, 0x19, voiceName, RcspProbe.DescribeVoiceName(voiceName?.Params ?? []));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 3：播放提示音命令的读回声探测（0x3D 为写命令，Request Data 读无文档预期，仅观察回声形态）
        await ProbeCustomReadAsync(session, "步骤 3：播放提示音(0x3D) 读回声探测", 0x3D).ConfigureAwait(false);

        // 结论：可读面 + 写命令清单（只列出，不发送）
        capture.Log("── probe-tone 结论 ──");
        capture.Log($"  提示音音量(0x1D): {DescribeReadability(toneVolume)}");
        capture.Log($"  语音名称(0x19): {DescribeReadability(voiceName)}");
        capture.Log("  播放提示音(0x3D) 读回声: 见步骤 3 判定（Quicky/qcy-h3s 均只定义写，无读格式文档）。");
        capture.Log("  已调研写命令（本次不发送，待主智能体决策是否真机触发）:");
        capture.Log("    播放提示音:  CUSTOM params = 3D 01 <toneID>   ← Quicky command/TonePlay.go、docs/protocol.md 0x3D；qcy-h3s play_tone（外部效果：按当前音量外放）");
        capture.Log("    查找耳机:    CUSTOM params = 4B 01 01           ← qcy-h3s find_earphones（响铃定位；外部效果：当前音量外放，over-ear 可能刺耳）");
        capture.Log("    写命令风险分级参考: qcy-h3s docs/COMANDOS-DESTRUTIVOS.md 将 0x4B/0x3D 列为 R2（外部音效），无破坏性。");

        LogLeftoverAndProbeDone(capture, session.Client, "probe-tone");
        return ExitOk;
    }

    // ───────────────────────── probe-multi（双设备连接只读探测） ─────────────────────────

    /// <summary>
    /// 双设备/多点连接只读探测（无写操作）：CMDID_1T2（0x24 读）→ GET_ADV_INFO 全掩码属性表。
    /// 0x24 的写命令在参考仓库均无出处，不实现；1T2 值的开/关极性参考仓库也未验证，只报原始字节。
    /// </summary>
    private static async Task<int> CmdProbeMultiAsync(string targetArgument)
    {
        using ProbeSession? session = await OpenProbeSessionAsync(targetArgument, "probe-multi 双设备连接只读探测").ConfigureAwait(false);
        if (session is null)
        {
            return ExitError;
        }
        DebugCapture capture = session.Capture;
        capture.Log("安全声明：本命令为纯读探测（0x24 Request Data 读 + GET_ADV_INFO 属性表），不含任何写操作。");

        // 步骤 1：CMDID_1T2 读（双设备连接状态，qcy-h3s 实测 H3S 回 24 01 00）
        capture.BeginPhase(0);
        capture.Log($"── 步骤 1：双设备连接(1T2)读（窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        Packet? oneToTwo = await SendAndListenAsync(session.Client, capture, "双设备连接(1T2)读",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x24]), ProbeReadWindow).ConfigureAwait(false);
        LogSpecializedOrGeneric(capture, 0x24, oneToTwo, RcspProbe.DescribeSwitchRead(0x24, oneToTwo?.Params ?? []));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 2：GET_ADV_INFO 全掩码属性表（连接相关 TLV 未必存在，全量列出供比对）
        capture.BeginPhase(0);
        capture.Log($"── 步骤 2：GET_ADV_INFO 全掩码属性表（FF FF FF FF，窗口 {ProbeReadWindow.TotalSeconds:0} 秒）──");
        var advAll = new Packet(OpCode.GetAdvInfo, parameters: [0xFF, 0xFF, 0xFF, 0xFF]);
        Packet? advResponse = await SendAndListenAsync(session.Client, capture, "GET_ADV_INFO 全掩码", advAll, ProbeReadWindow).ConfigureAwait(false);
        if (advResponse is null)
        {
            capture.Log("解读: 无响应（超时）。");
        }
        else
        {
            var entries = TlvParser.Parse(advResponse.Params);
            capture.Log($"解读: {entries.Count} 条 TLV —— {DescribeTlvInline(advResponse.Params)}");
            capture.Log("说明: ADV_INFO 属性表（AdvType 0..11）中没有「双设备/多点连接」类型——该状态只可能走 CUSTOM 命令（如 0x24）。");
        }

        // 结论
        capture.Log("── probe-multi 结论 ──");
        capture.Log($"  双设备连接(0x24) 读: {DescribeReadability(oneToTwo)}");
        capture.Log("  写命令: 参考仓库（Quicky 命令总表 / qcy-h3s 全量 set_* / qcy-menubar）均未出现 0x24 的写实现——" +
                    "按铁律不实现写探针；手机 App 的「双设备连接」开关走哪条命令待下一轮调研（APK ConstantCMDID 0x24 写路径）。");
        capture.Log("  若步骤 1 回空载荷/超时：本固件对该命令不可读，UI（阶段 4b）建议按「只写不读 + 乐观显示」路线另做调研后再接。");

        LogLeftoverAndProbeDone(capture, session.Client, "probe-multi");
        return ExitOk;
    }

    // ───────────────────────── 探针输出辅助 ─────────────────────────

    /// <summary>专用解读非空则打印，否则回退到 eqprobe2 的通用判定（AnalyzeCustomRead）。</summary>
    private static void LogSpecializedOrGeneric(DebugCapture capture, byte cmdId, Packet? response, string? specialized)
    {
        if (specialized is not null)
        {
            capture.Log($"解读: {specialized}");
            return;
        }
        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, cmdId], response);
        capture.Log($"判定: {verdict.Judgment}");
        capture.Log($"解读: {verdict.Interpretation}");
    }

    /// <summary>一条只读探测的可读性总结（结论区用）。</summary>
    private static string DescribeReadability(Packet? response)
    {
        if (response is null)
        {
            return "不可读（超时无应答）";
        }
        if (response.Params.Length == 0)
        {
            return "固件认识该命令但回空载荷（status=0 无数据）";
        }
        return $"可读（params({response.Params.Length}B) = {Hex(response.Params)}）";
    }

    /// <summary>探针收尾：滞留缓冲提示 + 完成标记（与 debug/eqprobe 系列的收尾一致）。</summary>
    private static void LogLeftoverAndProbeDone(DebugCapture capture, H3Client client, string probeName)
    {
        byte[] leftover = client.SnapshotRxBuffer();
        if (leftover.Length > 0)
        {
            capture.Log($"注意: 接收缓冲滞留 {leftover.Length}B（半截帧，后续分片未到齐）: {Hex(leftover)}");
        }
        capture.Log($"{probeName} 完成。");
    }
}
