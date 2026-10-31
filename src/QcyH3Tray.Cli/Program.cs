using System.Diagnostics;
using System.Globalization;
using System.Text;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Config;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Cli;

/// <summary>
/// QCY H3 命令行工具（阶段 2 真机自测通道，与 App 共用 Core）：
/// <list type="bullet">
/// <item><c>scan [超时秒]</c>：扫描并列出 BLE 设备（QCY 设备排前）；</item>
/// <item><c>info &lt;地址或名称&gt;</c>：连接并读取电量/降噪模式/EQ/游戏模式；</item>
/// <item><c>setmode &lt;模式&gt; [地址或名称]</c>：切换降噪模式并回读确认
///（含场景模式：通勤低/中/高、室内低/中/高、抗风）；</item>
/// <item><c>probe</c>：真机自检（蓝牙 radio 检查 + 5s 扫描，不连接）；</item>
/// <item><c>debug &lt;地址或名称&gt;</c>：抓包诊断（静默收 1s → 降噪读 → EQ 读，
/// 逐字节打印收发与解析结果，长期保留的诊断通道）；</item>
/// <item><c>eqprobe &lt;地址或名称&gt;</c>：EQ 读取实验（5 组探测找出设备不回 EQ 数据
/// 的原因，唯一写操作为默认预设全零偏移，长期保留的实验通道）。</item>
/// <item><c>eqprobe2 &lt;地址或名称&gt;</c>：EQ 读取二轮实验（调研驱动：feature map /
/// EQ 读变体 / 预设 ID 属性探针 / GATT 直读 / 全 ID 扫描，纯读无写，长期保留的实验通道）。</item>
/// <item><c>eqprobe3 &lt;地址或名称&gt;</c>：EQ v1 读写验证（读基线 → 写全零平线 → 等 1s
/// 读回对比 → 原样恢复基线 → 读回验证，打印四步对照表与结论；唯一写操作为全零平线与恢复）。</item>
/// <item><c>probe-off / probe-keys / probe-tone / probe-multi &lt;地址或名称&gt;</c>：
/// 阶段 4a 扩展功能探针（自动关机 / 按键映射 / 提示音 / 双设备连接，见 <see cref="Probes"/> 文件头）。</item>
/// </list>
/// 退出码：成功 0，失败 1，用法错误 2。所有失败路径输出可读中文错误。
/// </summary>
internal static partial class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;

    /// <summary>setmode 全部可选模式（六基础模式 + 场景模式，真机抓包 2026-10 证实场景组存在）。</summary>
    private const string ModeOptionsText = "关闭|通透|降噪低|降噪中|降噪高|通勤低|通勤中|通勤高|室内低|室内中|室内高|抗风|自适应";

    private static readonly TimeSpan TargetSearchTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 输出重定向等场景下设置编码可能失败，忽略
        }

        try
        {
            string command = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : "help";
            return command switch
            {
                "scan" => await CmdScanAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "info" => args.Length >= 2
                    ? await CmdInfoAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli info <地址或名称>"),
                "setmode" => args.Length >= 2
                    ? await CmdSetModeAsync(args.Skip(1).ToArray()).ConfigureAwait(false)
                    : UsageError($"用法: QcyH3Tray.Cli setmode <{ModeOptionsText}> [地址或名称]"),
                "probe" => await CmdProbeAsync().ConfigureAwait(false),
                "debug" => args.Length >= 2
                    ? await CmdDebugAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli debug <地址或名称>"),
                "eqprobe" => args.Length >= 2
                    ? await CmdEqProbeAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli eqprobe <地址或名称>"),
                "eqprobe2" => args.Length >= 2
                    ? await CmdEqProbe2Async(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli eqprobe2 <地址或名称>"),
                "eqprobe3" => args.Length >= 2
                    ? await CmdEqProbe3Async(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli eqprobe3 <地址或名称>"),
                "probe-off" => args.Length >= 2
                    ? await CmdProbeOffAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli probe-off <地址或名称>"),
                "probe-keys" => args.Length >= 2
                    ? await CmdProbeKeysAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli probe-keys <地址或名称>"),
                "probe-tone" => args.Length >= 2
                    ? await CmdProbeToneAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli probe-tone <地址或名称>"),
                "probe-multi" => args.Length >= 2
                    ? await CmdProbeMultiAsync(args[1]).ConfigureAwait(false)
                    : UsageError("用法: QcyH3Tray.Cli probe-multi <地址或名称>"),
                "help" => PrintUsage(),
                _ => UsageError($"未知命令「{args.FirstOrDefault()}」。\n{UsageText()}"),
            };
        }
        catch (BluetoothUnavailableException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return ExitError;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return ExitError;
        }
    }

    // ───────────────────────── scan ─────────────────────────

    private static async Task<int> CmdScanAsync(string[] args)
    {
        double seconds = 6;
        if (args.Length > 0
            && (!double.TryParse(args[0], CultureInfo.InvariantCulture, out seconds) || seconds <= 0))
        {
            return UsageError("扫描时长必须是正数（秒），例如: QcyH3Tray.Cli scan 5");
        }

        using var scanner = new BleScanner();
        Console.WriteLine($"正在扫描 BLE 设备（{seconds:0.#} 秒），请稍候…");
        List<BleScanResult> devices = await scanner.ScanAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

        List<BleScanResult> sorted = devices
            .OrderByDescending(IsQcyCandidate)
            .ThenByDescending(device => device.Rssi)
            .ToList();

        if (sorted.Count == 0)
        {
            Console.WriteLine("未发现任何 BLE 设备（请确认耳机已开机并离开充电盒、系统蓝牙已开启）。");
            return ExitOk;
        }

        Console.WriteLine($"发现 {sorted.Count} 台设备：");
        foreach (BleScanResult device in sorted)
        {
            Console.WriteLine(FormatDeviceLine(device));
        }
        return ExitOk;
    }

    // ───────────────────────── info ─────────────────────────

    private static async Task<int> CmdInfoAsync(string targetArgument)
    {
        (ulong Address, string Name)? target = await ResolveTargetAsync(targetArgument).ConfigureAwait(false);
        if (target is null)
        {
            return ExitError;
        }
        (ulong address, string name) = target.Value;

        using var manager = new ConnectionManager { AutoReconnect = false };
        int? battery = null;
        AncModeInfo? noiseModeInfo = null;
        EqInfo? eq = null;
        bool? gameMode = null;
        manager.StatusText += text => Console.WriteLine(text);
        manager.BatteryChanged += percent => battery = percent;
        manager.DeviceDataChanged += data =>
        {
            if (data.NoiseModeInfo is AncModeInfo info)
            {
                noiseModeInfo = info;
            }
            if (data.EqOffsets is not null)
            {
                eq = new EqInfo(data.EqPresetId ?? EqPresets.Custom, data.EqOffsets);
            }
            if (data.GameMode is bool game)
            {
                gameMode = game;
            }
        };

        manager.SetTarget(address, name);
        bool connected = await manager.ConnectAsync().WaitAsync(ConnectTimeout).ConfigureAwait(false);
        if (!connected)
        {
            Console.Error.WriteLine($"连接失败：{manager.CurrentStatus}");
            return ExitError;
        }

        try
        {
            Console.WriteLine($"── {name}（{BleScanResult.FormatAddress(address)}）设备信息 ──");
            Console.WriteLine($"电量: {(battery is int percent ? $"{percent}%" : "未知")}");
            // 展示优先用完整解码（含场景模式，如「室内·高」）；旧索引仅作兜底。
            Console.WriteLine($"降噪模式: {(noiseModeInfo?.DisplayName ?? "未知")}");

            if (eq is not null)
            {
                string presetName = eq.PresetId < EqPresets.Names.Length
                    ? EqPresets.Names[eq.PresetId]
                    : $"未知(0x{eq.PresetId:X2})";
                Console.WriteLine($"EQ: 预设 {presetName}（0x{eq.PresetId:X2}）");
                for (int i = 0; i < eq.Offsets.Length; i++)
                {
                    Console.WriteLine($"  {EqBands.Labels[i],-7} {eq.Offsets[i] / 100.0:F1} dB");
                }
            }
            else
            {
                Console.WriteLine("EQ: 未知");
            }

            Console.WriteLine($"游戏模式: {(gameMode is bool game ? (game ? "开" : "关") : "未知")}");
        }
        finally
        {
            manager.Disconnect();
        }

        SaveTargetConfig(address, name);
        return ExitOk;
    }

    // ───────────────────────── setmode ─────────────────────────

    private static async Task<int> CmdSetModeAsync(string[] args)
    {
        string modeArgument = args[0];
        byte[]? modeBytes = modeArgument switch
        {
            "关闭" => AncMode.Off,
            "通透" => AncMode.Transparency,
            "降噪低" => AncMode.NoiseCancelLow,
            "降噪中" => AncMode.NoiseCancelMedium,
            "降噪高" => AncMode.NoiseCancelHigh,
            "通勤低" => AncMode.CommutingLow,
            "通勤中" => AncMode.CommutingMedium,
            "通勤高" => AncMode.CommutingHigh,
            "室内低" => AncMode.IndoorLow,
            "室内中" => AncMode.IndoorMedium,
            "室内高" => AncMode.IndoorHigh,
            "抗风" => AncMode.AntiWind,
            "自适应" => AncMode.Adaptive,
            _ => null,
        };
        if (modeBytes is null)
        {
            return UsageError($"未知降噪模式「{modeArgument}」。可选：{ModeOptionsText}");
        }
        // 回读对比用完整解码（AncModeInfo 记录相等性），场景模式（通勤/室内/抗风）同样可确认。
        AncModeInfo? expectedInfo = AncMode.Decode(modeBytes);

        (ulong Address, string Name)? target = args.Length >= 2
            ? await ResolveTargetAsync(args[1]).ConfigureAwait(false)
            : LoadTargetConfig();
        if (target is null)
        {
            Console.Error.WriteLine(
                "未指定设备：请在命令后附加 <地址或名称>，或先用 info <地址或名称> 保存默认目标。");
            return ExitError;
        }
        (ulong address, string name) = target.Value;

        using var manager = new ConnectionManager { AutoReconnect = false };
        manager.StatusText += text => Console.WriteLine(text);
        manager.SetTarget(address, name);

        bool connected = await manager.ConnectAsync().WaitAsync(ConnectTimeout).ConfigureAwait(false);
        if (!connected)
        {
            Console.Error.WriteLine($"连接失败：{manager.CurrentStatus}");
            return ExitError;
        }

        try
        {
            NoiseModeSetResult? result = await manager
                .SetNoiseModeAsync(modeBytes)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            if (result is null || !result.Acknowledged)
            {
                Console.Error.WriteLine("耳机没有响应，降噪模式可能未生效。请重试。");
                return ExitError;
            }

            if (result.ReadBackInfo is AncModeInfo readBack)
            {
                if (expectedInfo is not null && readBack == expectedInfo)
                {
                    Console.WriteLine($"降噪模式已切换为「{readBack.DisplayName}」（回读一致）。");
                }
                else
                {
                    Console.WriteLine(
                        $"降噪模式已写入「{modeArgument}」，但回读为「{readBack.DisplayName}」，请手动确认。");
                }
            }
            else
            {
                Console.WriteLine($"降噪模式已写入「{modeArgument}」（回读无响应，可能仍已生效）。");
            }
        }
        finally
        {
            manager.Disconnect();
        }

        SaveTargetConfig(address, name);
        return ExitOk;
    }

    // ───────────────────────── probe ─────────────────────────

    /// <summary>真机自检：只检查蓝牙 radio + 扫描 5 秒，绝不连接。</summary>
    private static async Task<int> CmdProbeAsync()
    {
        Console.WriteLine("QCY H3 CLI 自检（只扫描，不连接）");
        await BluetoothAvailability.EnsureAvailableAsync().ConfigureAwait(false);
        Console.WriteLine("蓝牙 radio：可用");

        using var scanner = new BleScanner();
        List<BleScanResult> devices = await scanner.ScanAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        List<BleScanResult> qcyDevices = devices.Where(IsQcyCandidate).ToList();
        Console.WriteLine($"扫描完成：共 {devices.Count} 台 BLE 设备，其中疑似 QCY {qcyDevices.Count} 台。");
        foreach (BleScanResult device in qcyDevices)
        {
            Console.WriteLine($"  {FormatDeviceLine(device)}");
        }
        if (qcyDevices.Count == 0)
        {
            Console.WriteLine("附近未发现 QCY 设备（耳机不在线时属正常现象），命令本身工作正常。");
        }
        return ExitOk;
    }

    // ───────────────────────── debug（抓包诊断，长期保留） ─────────────────────────

    private static readonly TimeSpan DebugSilentWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DebugProbeWindow = TimeSpan.FromSeconds(3);

    /// <summary>一次探测（一条读命令 + 一个接收窗口）的统计快照。</summary>
    private sealed record ProbeStats(
        string Label,
        byte[] RequestParams,
        byte Sn,
        int RawChunks,
        int DecodedFrames,
        int DecodeFailures,
        bool SnMatched,
        Packet? Response);

    /// <summary>
    /// 抓包诊断（真机排查专用）：连接 + 握手后，先静默收 1 秒观察设备主动推送，
    /// 再依次发降噪读 FF FE 01 17 与 EQ 读 FF FE 01 22（每条命令后收 3 秒）。全程把
    /// 收到的每个原始字节块、每个解析结果（含解析失败被丢弃的帧）以及 sn 匹配判定
    /// 带时间戳完整打印，最后输出两条命令各自的统计结论。只读不改：不写配置、
    /// 不影响其它命令的任何行为。
    /// </summary>
    private static async Task<int> CmdDebugAsync(string targetArgument)
    {
        (ulong Address, string Name)? target = await ResolveTargetAsync(targetArgument).ConfigureAwait(false);
        if (target is null)
        {
            return ExitError;
        }
        (ulong address, string name) = target.Value;

        var capture = new DebugCapture();
        using var transport = new BleTransport();
        transport.DataReceived += capture.OnRawChunk;   // 先订原始块：每块先打印、再进协议解析
        using var client = new H3Client(transport);
        client.PacketTraced += capture.OnFrameTraced;

        Console.WriteLine($"── debug 抓包：{name}（{BleScanResult.FormatAddress(address)}）──");
        try
        {
            await transport.ConnectAsync(address).WaitAsync(ConnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"连接超时（{ConnectTimeout.TotalSeconds:0} 秒）：设备可能已关机、入盒或不广播。");
            return ExitError;
        }
        capture.Log("GATT 已连接（服务 A002，写特征 0001，通知特征 0002）");

        Packet? handshake = await client.HandshakeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (handshake is null)
        {
            capture.Log("握手（NOTIFY_COMMUNICATION_WAY 00 00）2 秒无响应——协议层无法继续诊断，请确认设备未被其它 App 占用。");
            return ExitError;
        }
        capture.Log($"握手成功: {DescribePacket(handshake)}");

        // 阶段 1：静默 1 秒，只收不发，观察设备主动推送
        capture.BeginPhase(0);
        capture.Log($"── 静默监听 {DebugSilentWindow.TotalSeconds:0} 秒（只收不发）──");
        await Task.Delay(DebugSilentWindow).ConfigureAwait(false);
        ProbeStats silence = capture.EndPhase("静默监听", [], null);
        capture.Log($"静默期统计: 原始块 {silence.RawChunks} 个，解析出 {silence.DecodedFrames} 个包（解析失败丢弃 {silence.DecodeFailures} 个）");

        // 阶段 2/3：降噪读、EQ 读（与 info 命令同参数，区别在于全量抓包输出）
        ProbeStats noiseStats = await ProbeAsync(client, capture, "降噪读", [0xFE, 0x01, 0x17]).ConfigureAwait(false);
        ProbeStats eqStats = await ProbeAsync(client, capture, "EQ 读", [0xFE, 0x01, 0x22]).ConfigureAwait(false);

        byte[] leftover = client.SnapshotRxBuffer();
        if (leftover.Length > 0)
        {
            capture.Log($"注意: 接收缓冲滞留 {leftover.Length}B（半截帧，后续分片未到齐）: {Hex(leftover)}");
        }

        capture.Log("── 结论 ──");
        foreach (var stats in new[] { noiseStats, eqStats })
        {
            capture.Log(
                $"  {Hex(stats.RequestParams)}（{stats.Label}，sn=0x{stats.Sn:X2}）: 窗口内原始块 {stats.RawChunks} 个、" +
                $"解析出 {stats.DecodedFrames} 个包（解析失败丢弃 {stats.DecodeFailures} 个）、sn 匹配={(stats.SnMatched ? "是" : "否")}。");
            capture.Log($"    SendAsync 结果: {DescribeProbeResponse(stats)}");
        }

        return ExitOk;
    }

    /// <summary>
    /// 发一条 CUSTOM 读命令并监听整整 <see cref="DebugProbeWindow"/>：SendAsync 的
    /// 超时同为 3 秒，但本方法固定再等满 3 秒窗口后才统计，保证"命令后收 3 秒"。
    /// </summary>
    private static async Task<ProbeStats> ProbeAsync(
        H3Client client, DebugCapture capture, string label, byte[] requestParams)
    {
        var packet = new Packet(OpCode.Custom, parameters: requestParams);
        packet.Sn = client.NextSn();   // 显式取号：CLI 侧能先打印完整 TX 帧（SendAsync 见非 0 sn 不会重取）
        byte sn = packet.Sn;
        byte[] txFrame = packet.Encode();

        capture.BeginPhase(sn);
        capture.Log($"TX {txFrame.Length}B: {Hex(txFrame)}   ← {label}（opCode=0xFF params={Hex(requestParams)}，等待 {DebugProbeWindow.TotalSeconds:0} 秒）");

        Packet? response = null;
        try
        {
            Task<Packet?> pending = client.SendAsync(packet, DebugProbeWindow);
            await Task.Delay(DebugProbeWindow).ConfigureAwait(false);
            response = await pending.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            capture.Log($"发送失败: {exception.Message}");
        }

        return capture.EndPhase(label, requestParams, response);
    }

    /// <summary>
    /// debug 会话采集器：订阅传输层原始块与 <see cref="H3Client.PacketTraced"/>，
    /// 每行带会话内相对时间戳（秒，会话起点 = 采集器创建时）打印；同时按当前阶段
    /// （静默 / 某条命令的接收窗口）累计统计。蓝牙回调线程与主流程都会触发打印，
    /// 内部加锁保证统计与行输出一致。
    /// </summary>
    private sealed class DebugCapture
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private byte _probeSn;
        private int _rawChunks;
        private int _decodedFrames;
        private int _decodeFailures;
        private bool _snMatched;

        /// <summary>开始一个接收阶段：<paramref name="probeSn"/> 为 0 表示静默监听。</summary>
        public void BeginPhase(byte probeSn)
        {
            lock (_gate)
            {
                _probeSn = probeSn;
                _rawChunks = 0;
                _decodedFrames = 0;
                _decodeFailures = 0;
                _snMatched = false;
            }
        }

        public void Log(string message)
        {
            lock (_gate)
            {
                Console.WriteLine($"[{Timestamp()}] {message}");
            }
        }

        /// <summary>transport.DataReceived：打印原始字节块并计数。</summary>
        public void OnRawChunk(byte[] chunk)
        {
            lock (_gate)
            {
                _rawChunks++;
                Console.WriteLine($"[{Timestamp()}] {chunk.Length}B: {Hex(chunk)}");
            }
        }

        /// <summary>client.PacketTraced：打印解析出的包字段与 sn 匹配判定，并计入统计。</summary>
        public void OnFrameTraced(FrameTrace trace)
        {
            lock (_gate)
            {
                if (trace.Packet is not Packet packet)
                {
                    _decodeFailures++;
                    Console.WriteLine($"[{Timestamp()}]     解析: 失败（paramLen 或帧尾 EF 非法，帧已丢弃）: {Hex(trace.Frame)}");
                    return;
                }

                _decodedFrames++;
                Console.WriteLine($"[{Timestamp()}]     解析: {DescribePacket(packet)}");
                if (trace.MatchedPending)
                {
                    if (_probeSn != 0 && packet.Sn == _probeSn)
                    {
                        _snMatched = true;
                    }
                    Console.WriteLine($"[{Timestamp()}]     匹配: sn=0x{packet.Sn:X2} 命中 pending 请求");
                }
                else
                {
                    string pending = trace.PendingSns.Count > 0
                        ? string.Join(", ", trace.PendingSns.Select(item => $"0x{item:X2}"))
                        : "空";
                    Console.WriteLine($"[{Timestamp()}]     匹配: sn=0x{packet.Sn:X2} 未命中 pending（当前挂起: {pending}）→ 按设备主动推送处理");
                }
            }
        }

        /// <summary>结束当前阶段并取走统计（重置计数器）。</summary>
        public ProbeStats EndPhase(string label, byte[] requestParams, Packet? response)
        {
            lock (_gate)
            {
                var stats = new ProbeStats(
                    label, requestParams, _probeSn, _rawChunks, _decodedFrames, _decodeFailures, _snMatched, response);
                _probeSn = 0;
                _rawChunks = 0;
                _decodedFrames = 0;
                _decodeFailures = 0;
                _snMatched = false;
                return stats;
            }
        }

        /// <summary>调用方须持有 <see cref="_gate"/>。</summary>
        private string Timestamp() => $"+{_clock.Elapsed.TotalSeconds:0.000}s";
    }

    // ───────────────────────── debug 输出辅助 ─────────────────────────

    /// <summary>Packet 字段一行式描述（解析结果打印共用）。</summary>
    private static string DescribePacket(Packet packet)
    {
        string xm = packet.XmOpCode is byte x ? $"0x{x:X2}" : "-";
        return $"opCode=0x{packet.OpCode:X2} type={(packet.Type == Packet.TypeResponse ? "响应" : "命令")} " +
               $"hasResponse={packet.HasResponse} status={packet.Status} sn=0x{packet.Sn:X2} xm={xm} " +
               $"params({packet.Params.Length}B)={Hex(packet.Params)}";
    }

    /// <summary>结论行里对 SendAsync 结果的人类可读解释。</summary>
    private static string DescribeProbeResponse(ProbeStats stats)
    {
        if (stats.Response is not Packet response)
        {
            return stats.DecodedFrames == 0
                ? "null —— 窗口内未收到任何可解析包（命令超时无应答）"
                : "null —— 有包到达但 sn 都未命中（或解析失败被丢弃），命令按超时处理";
        }

        var text = new StringBuilder(DescribePacket(response));
        if (stats.RequestParams is [0xFE, 0x01, 0x17])
        {
            AncModeInfo? info = AncMode.Decode(response.Params);
            text.Append(info is not null ? $"（降噪模式: {info.DisplayName}）" : "（降噪模式负载不合法）");
        }
        else if (stats.RequestParams is [0xFE, 0x01, 0x22])
        {
            text.Append(response.Params.Length >= 2 && response.Params[0] == 0x22
                ? $"（EQ 响应: 声明长度={response.Params[1]}，预设 ID=0x{(response.Params.Length >= 3 ? response.Params[2] : 0):X2}）"
                : "（EQ 响应首字节不是 0x22，不合预期）");
        }
        return text.ToString();
    }

    /// <summary>大写十六进制、空格分隔（debug 打印格式，如 "FE DC BA"）。</summary>
    private static string Hex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
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

    // ───────────────────────── eqprobe（EQ 读取实验，长期保留） ─────────────────────────

    private static readonly TimeSpan EqProbeStepGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EqProbeShortWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EqProbeReadWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EqProbeSysInfoWindow = TimeSpan.FromSeconds(3);

    /// <summary>EQ 样式回包判定标准：params[0]==0x22 且 params 长度 ≥ 52。</summary>
    private const int EqProbeMinParamsLength = 52;

    /// <summary>
    /// EQ 读取实验（真机排查专用）：背景是真机抓包发现设备对 EQ 读命令
    /// <c>FE 01 22</c> 回 status=0 但 params 空载荷且无后续推送。本命令连接握手后
    /// 依次做 5 组实验，每步打印收到的完整原始帧与解析结果（复用 debug 的抓包输出），
    /// 步间间隔 1 秒，最后总结哪组实验拿到了 EQ 数据：
    /// <list type="number">
    /// <item>基线重试：FE 01 22，等 5 秒（排除"响应慢于 3 秒窗口"）；</item>
    /// <item>连发两条 FE 01 22（间隔 300ms），自第一条起共等 5 秒；</item>
    /// <item>GET_SYS_INFO（opCode 0x07）读 EQ（function=0x07，掩码 Eq|HighAndBass|EqPresetValue）；</item>
    /// <item>先写默认预设（preset 0x01、10 段增益全 0——听感无变化，唯一允许的写操作）再读；</item>
    /// <item>GET_ADV_INFO（0xC1）掩码试探：1&lt;&lt;7 与 0xFFFFFFFF 各等 2 秒。</item>
    /// </list>
    /// EQ 判定标准：响应 params[0]==0x22 且 params 长度 ≥ 52。
    /// </summary>
    private static async Task<int> CmdEqProbeAsync(string targetArgument)
    {
        (ulong Address, string Name)? target = await ResolveTargetAsync(targetArgument).ConfigureAwait(false);
        if (target is null)
        {
            return ExitError;
        }
        (ulong address, string name) = target.Value;

        var capture = new DebugCapture();
        using var transport = new BleTransport();
        transport.DataReceived += capture.OnRawChunk;   // 先订原始块：每块先打印、再进协议解析
        using var client = new H3Client(transport);
        client.PacketTraced += capture.OnFrameTraced;
        var eqHits = new EqHitCollector(capture);
        client.PacketTraced += eqHits.OnFrameTraced;    // 并行订阅：按实验分桶记录 EQ 样式回包

        Console.WriteLine($"── eqprobe EQ 读取实验：{name}（{BleScanResult.FormatAddress(address)}）──");
        try
        {
            await transport.ConnectAsync(address).WaitAsync(ConnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"连接超时（{ConnectTimeout.TotalSeconds:0} 秒）：设备可能已关机、入盒或不广播。");
            return ExitError;
        }
        capture.Log("GATT 已连接（服务 A002，写特征 0001，通知特征 0002）");

        Packet? handshake = await client.HandshakeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (handshake is null)
        {
            capture.Log("握手（NOTIFY_COMMUNICATION_WAY 00 00）2 秒无响应——无法继续实验，请确认设备未被其它 App 占用。");
            return ExitError;
        }
        capture.Log($"握手成功: {DescribePacket(handshake)}");
        capture.Log("安全声明：唯一写操作为实验 4 的默认预设（preset 0x01、10 段增益全 0，听感无变化），其余步骤只读。");

        var steps = new List<(string Label, ProbeStats Stats, int EqCount)>();

        // 实验 1：基线重试（FE 01 22，窗口 5 秒——确认不是"响应慢于 3 秒"）
        eqHits.BeginStep("1 基线重试");
        capture.BeginPhase(0);
        capture.Log($"── 实验 1：基线重试（FE 01 22，接收窗口 {EqProbeReadWindow.TotalSeconds:0} 秒）──");
        Task<Packet?> pending1 = StartTracedSend(client, capture, "EQ 读·基线",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x22]), EqProbeReadWindow);
        await Task.Delay(EqProbeReadWindow).ConfigureAwait(false);
        ProbeStats stats1 = capture.EndPhase("1 基线重试", [0xFE, 0x01, 0x22], await pending1.ConfigureAwait(false));
        steps.Add(("1 基线重试（5s）", stats1, eqHits.CurrentCount));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 2：连发两条（间隔 300ms，有的固件第二条才带数据），自第一条起共等 5 秒
        eqHits.BeginStep("2 连发两次");
        capture.BeginPhase(0);
        capture.Log("── 实验 2：连发两条 FE 01 22（间隔 300ms，共等 5 秒）──");
        Task<Packet?> pending2a = StartTracedSend(client, capture, "EQ 读·连发第 1 条",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x22]), EqProbeReadWindow);
        await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
        Task<Packet?> pending2b = StartTracedSend(client, capture, "EQ 读·连发第 2 条",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x22]), TimeSpan.FromSeconds(4.7));
        await Task.Delay(TimeSpan.FromSeconds(4.7)).ConfigureAwait(false);
        Packet? first = await pending2a.ConfigureAwait(false);
        Packet? second = await pending2b.ConfigureAwait(false);
        ProbeStats stats2 = capture.EndPhase("2 连发两次", [0xFE, 0x01, 0x22], first ?? second);
        steps.Add(("2 连发两次（间隔300ms）", stats2, eqHits.CurrentCount));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 3：系统信息通道 GET_SYS_INFO（opCode 0x07）——字节构造抄自 Python
        // h3_device.get_sys_info: bytes([function & 0xFF]) + mask.to_bytes(4, "big")，
        // function=SysFunc.EQ(7)，掩码取 EQ 相关属性位（rcsp.SysAttr: EQ=4, HIGH_AND_BASS=11, EQ_PRESET_VALUE=12）。
        eqHits.BeginStep("3 系统信息通道");
        capture.BeginPhase(0);
        uint eqAttrMask = (1u << SysAttr.Eq) | (1u << SysAttr.HighAndBass) | (1u << SysAttr.EqPresetValue);
        capture.Log($"── 实验 3：GET_SYS_INFO 读 EQ（function=0x{SysFunc.Eq:X2}，掩码=0x{eqAttrMask:X8}，窗口 {EqProbeSysInfoWindow.TotalSeconds:0} 秒）──");
        var sysInfo = new Packet(OpCode.GetSysInfo, parameters:
        [
            SysFunc.Eq,
            (byte)(eqAttrMask >> 24),
            (byte)(eqAttrMask >> 16),
            (byte)(eqAttrMask >> 8),
            (byte)eqAttrMask,
        ]);
        Task<Packet?> pending3 = StartTracedSend(client, capture, "GET_SYS_INFO 读 EQ", sysInfo, EqProbeSysInfoWindow);
        await Task.Delay(EqProbeSysInfoWindow).ConfigureAwait(false);
        ProbeStats stats3 = capture.EndPhase("3 系统信息通道", sysInfo.Params, await pending3.ConfigureAwait(false));
        steps.Add(("3 GET_SYS_INFO(function=7)", stats3, eqHits.CurrentCount));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 4：先写后读（假设：固件要 EQ 被写过一次才回数据）。
        // 唯一允许的写操作：默认预设 0x01、10 段增益全 0（偏移全 0，听感无变化）。
        eqHits.BeginStep("4 先写后读");
        capture.BeginPhase(0);
        capture.Log($"── 实验 4：先写默认预设 0x01（全零偏移，听感无变化）→ 等 {EqProbeStepGap.TotalSeconds:0}s → 再发 FE 01 22 ──");
        byte[] writePayload = H3Client.BuildSetEqPayload(EqPresets.GetOffsets(EqPresets.Default), EqPresets.Default);
        Task<Packet?> pending4w = StartTracedSend(client, capture, "EQ 写·默认预设（全零）",
            new Packet(OpCode.Custom, parameters: writePayload), TimeSpan.FromSeconds(2));
        await Task.Delay(EqProbeStepGap).ConfigureAwait(false);
        Packet? writeAck = await pending4w.ConfigureAwait(false);
        capture.Log($"写入结果: {(writeAck is null ? "无响应（写入可能未生效）" : DescribePacket(writeAck))}");
        capture.Log("已写入，现在重发 EQ 读命令 …");
        Task<Packet?> pending4r = StartTracedSend(client, capture, "EQ 读·写后",
            new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x22]), EqProbeSysInfoWindow);
        await Task.Delay(EqProbeSysInfoWindow).ConfigureAwait(false);
        ProbeStats stats4 = capture.EndPhase("4 先写后读", [0xFE, 0x01, 0x22], await pending4r.ConfigureAwait(false));
        steps.Add(("4 先写默认预设再读", stats4, eqHits.CurrentCount));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 5：GET_ADV_INFO（0xC1）掩码试探——猜 EQ 信息挂在 ADV 属性位上
        eqHits.BeginStep("5a ADV_INFO 掩码 1<<7");
        capture.BeginPhase(0);
        capture.Log("── 实验 5：GET_ADV_INFO（0xC1）掩码试探（先 00 00 00 80 = 1<<7，各等 2 秒）──");
        var adv7 = new Packet(OpCode.GetAdvInfo, parameters: [0x00, 0x00, 0x00, 0x80]);
        Task<Packet?> pending5a = StartTracedSend(client, capture, "GET_ADV_INFO 掩码 00 00 00 80", adv7, EqProbeShortWindow);
        await Task.Delay(EqProbeShortWindow).ConfigureAwait(false);
        ProbeStats stats5a = capture.EndPhase("5a ADV_INFO 1<<7", adv7.Params, await pending5a.ConfigureAwait(false));
        steps.Add(("5a GET_ADV_INFO 掩码 1<<7", stats5a, eqHits.CurrentCount));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        eqHits.BeginStep("5b ADV_INFO 掩码全 1");
        capture.BeginPhase(0);
        capture.Log("── 实验 5（续）：GET_ADV_INFO 掩码 FF FF FF FF（全部属性）──");
        var advAll = new Packet(OpCode.GetAdvInfo, parameters: [0xFF, 0xFF, 0xFF, 0xFF]);
        Task<Packet?> pending5b = StartTracedSend(client, capture, "GET_ADV_INFO 掩码 FF FF FF FF", advAll, EqProbeShortWindow);
        await Task.Delay(EqProbeShortWindow).ConfigureAwait(false);
        ProbeStats stats5b = capture.EndPhase("5b ADV_INFO 全掩码", advAll.Params, await pending5b.ConfigureAwait(false));
        steps.Add(("5b GET_ADV_INFO 掩码全 1", stats5b, eqHits.CurrentCount));

        byte[] leftover = client.SnapshotRxBuffer();
        if (leftover.Length > 0)
        {
            capture.Log($"注意: 接收缓冲滞留 {leftover.Length}B（半截帧，后续分片未到齐）: {Hex(leftover)}");
        }

        capture.Log($"── eqprobe 总结（EQ 判定标准：params[0]==0x22 且 params 长度 ≥ {EqProbeMinParamsLength}）──");
        var winners = new List<string>();
        foreach (var (label, stats, eqCount) in steps)
        {
            capture.Log(
                $"  实验 {label}: 窗口内原始块 {stats.RawChunks} 个、解析出 {stats.DecodedFrames} 个包（解析失败丢弃 {stats.DecodeFailures} 个）、" +
                $"EQ 样式回包 {eqCount} 个。");
            capture.Log($"    SendAsync 结果: {DescribeProbeResponse(stats)}");
            if (eqCount > 0)
            {
                winners.Add(label);
            }
        }
        capture.Log(winners.Count > 0
            ? $"结论: 拿到 EQ 数据的实验 —— {string.Join("、", winners)}。"
            : "结论: 全部实验均未拿到 EQ 数据（无 params[0]=0x22 且长度≥52 的回包）——请对照上方各步原始帧继续分析前置条件。");

        return ExitOk;
    }

    /// <summary>
    /// 显式取 sn、打印完整 TX 帧（debug 风格）并发出；返回 SendAsync 任务——调用方先
    /// Task.Delay 等满接收窗口再 await 结果，保证"命令发出后收满 N 秒"。
    /// </summary>
    private static Task<Packet?> StartTracedSend(
        H3Client client, DebugCapture capture, string label, Packet packet, TimeSpan timeout)
    {
        packet.Sn = client.NextSn();   // 显式取号：CLI 侧能先打印完整 TX 帧（SendAsync 见非 0 sn 不会重取）
        byte[] txFrame = packet.Encode();
        capture.Log($"TX {txFrame.Length}B: {Hex(txFrame)}   ← {label}" +
                    $"（opCode=0x{packet.OpCode:X2} params={Hex(packet.Params)}，超时 {timeout.TotalSeconds:0.#} 秒）");
        try
        {
            return client.SendAsync(packet, timeout);
        }
        catch (Exception exception)
        {
            capture.Log($"发送失败: {exception.Message}");
            return Task.FromResult<Packet?>(null);
        }
    }

    /// <summary>实验步间间隔（1 秒），给设备留出消化上一条命令的时间。</summary>
    private static async Task EqProbeGapAsync(DebugCapture capture)
    {
        capture.Log($"（步间间隔 {EqProbeStepGap.TotalSeconds:0} 秒）");
        await Task.Delay(EqProbeStepGap).ConfigureAwait(false);
    }

    /// <summary>
    /// EQ 样式回包采集器：与 <see cref="DebugCapture"/> 并行订阅 <see cref="H3Client.PacketTraced"/>，
    /// 按"当前实验"分桶记录满足判定标准（params[0]==0x22 且长度 ≥ <see cref="EqProbeMinParamsLength"/>）
    /// 的回包，命中时立即高亮打印完整 params。蓝牙回调线程触发，内部加锁。
    /// </summary>
    private sealed class EqHitCollector
    {
        private readonly DebugCapture _capture;
        private readonly object _gate = new();
        private string _step = "准备";
        private List<Packet> _current = [];
        private int _total;

        public EqHitCollector(DebugCapture capture) => _capture = capture;

        /// <summary>开启下一个实验桶（此后到达的命中计入新实验）。</summary>
        public void BeginStep(string step)
        {
            lock (_gate)
            {
                _step = step;
                _current = [];
            }
        }

        /// <summary>当前实验桶的命中数（步骤结束时读取）。</summary>
        public int CurrentCount
        {
            get
            {
                lock (_gate)
                {
                    return _current.Count;
                }
            }
        }

        /// <summary>H3Client.PacketTraced 处理器：判定并记录 EQ 样式回包。</summary>
        public void OnFrameTraced(FrameTrace trace)
        {
            if (trace.Packet is not Packet packet
                || packet.Params.Length < EqProbeMinParamsLength
                || packet.Params[0] != 0x22)
            {
                return;
            }

            List<Packet> bucket;
            lock (_gate)
            {
                _total++;
                bucket = _current;
                _capture.Log($"★ EQ 样式回包（实验 {_step}，累计第 {_total} 个命中）: " +
                             $"params({packet.Params.Length}B) = {Hex(packet.Params)}");
            }
            bucket.Add(packet);
        }
    }

    // ───────────────────────── eqprobe2（EQ 读取二轮实验，调研驱动） ─────────────────────────

    private static readonly TimeSpan Eq2ProbeWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Eq2InfoWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Eq2SweepWindow = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan Eq2GattReadTimeout = TimeSpan.FromSeconds(4);

    /// <summary>直读候选特征：EQ 直读写特征（Quicky/menubar 路线，Quicky service.md 标注 read/write）。</summary>
    private static readonly Guid EqCharacteristicUuid = new("0000000b-0000-1000-8000-00805f9b34fb");

    /// <summary>单个实验步骤的记录（汇总用）。<see cref="Cmd"/> = 本步探测的 command 字节（非 CUSTOM 读步骤为 null）。</summary>
    private sealed record Eq2Step(string Label, ProbeStats? Stats, byte? Cmd, ProbeVerdict Verdict);

    /// <summary>
    /// EQ 读取二轮实验（真机排查专用，纯读——本命令不含任何写操作）。基于三个参考仓库的
    /// 调研结果设计（Quicky / qcy-menubar / qcy-h3s-windows），不重复第一轮 eqprobe 已做的
    /// 5 组实验。连接握手后依次执行：
    /// <list type="number">
    /// <item>GET_TARGET_FEATURE_MAP（opCode 2）——固件声明的 feature 位图；</item>
    /// <item>GET_TARGET_INFO（opCode 3）——固件版本等上下文（与 H3S 参考固件 30 03 00 01 08 对照）；</item>
    /// <item>EQ 读变体（Quicky 调研所得，各等 3 秒）：FE 01 20（EQ v1）、FE 01 46/47（左右通道）、
    /// FE 01 44（自定义 EQ 槽位数）、FE 01 27（自适应 EQ 状态）；判定 = params[0]==cmd 且
    /// 长度 ≥ 52（同第一轮），另按 Quicky 响应格式（[eqType][masterGain][7B/段]）加一套解读；</item>
    /// <item>GET_SYS_INFO（opCode 7）单属性探针（第一轮只试过组合掩码 0x001810）：
    /// mask=1&lt;&lt;12（仅 EqPresetValue——"当前预设 ID"查询）、mask=1&lt;&lt;4（仅 Eq）、
    /// mask=1&lt;&lt;0|1&lt;&lt;12（电量锚点 + 预设，验证"掩码整体被拒"还是"单属性被门控"）；</item>
    /// <item>GATT 特征枚举 + 直读（Quicky/menubar 路线）：枚举 A002 服务全部特征，
    /// 逐个直读带 READ 属性的特征（000B EQ 优先）——绕过 RCSP 通道直接拿 EQ 字节；</item>
    /// <item>FE 01 01..0x63 全 ID 可读性扫描（qcy-h3s-windows 方法论，1.2s/ID）：
    /// 划定本固件可读面，区分"EQ 读独坏"与"CUSTOM 读全坏"（已知 0x17 降噪可读，作阳性对照）。</item>
    /// </list>
    /// 每步打印：TX 帧、RX 原始块、解析、命中判定、人类可读解读；最后总结哪一步拿到 EQ 信息。
    /// </summary>
    private static async Task<int> CmdEqProbe2Async(string targetArgument)
    {
        (ulong Address, string Name)? target = await ResolveTargetAsync(targetArgument).ConfigureAwait(false);
        if (target is null)
        {
            return ExitError;
        }
        (ulong address, string name) = target.Value;

        var capture = new DebugCapture();
        using var transport = new BleTransport();
        transport.DataReceived += capture.OnRawChunk;   // 先订原始块：每块先打印、再进协议解析
        using var client = new H3Client(transport);
        client.PacketTraced += capture.OnFrameTraced;

        Console.WriteLine($"── eqprobe2 EQ 读取二轮实验（纯读）：{name}（{BleScanResult.FormatAddress(address)}）──");
        try
        {
            await transport.ConnectAsync(address).WaitAsync(ConnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"连接超时（{ConnectTimeout.TotalSeconds:0} 秒）：设备可能已关机、入盒或不广播。");
            return ExitError;
        }
        capture.Log("GATT 已连接（服务 A002，写特征 0001，通知特征 0002）");

        Packet? handshake = await client.HandshakeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (handshake is null)
        {
            capture.Log("握手（NOTIFY_COMMUNICATION_WAY 00 00）2 秒无响应——无法继续实验，请确认设备未被其它 App 占用。");
            return ExitError;
        }
        capture.Log($"握手成功: {DescribePacket(handshake)}");
        capture.Log("安全声明：本命令为纯读实验（GET / Request Data / GATT 读），不含任何写操作，不改变耳机任何设置。");

        var steps = new List<Eq2Step>();

        // 实验 1：GET_TARGET_FEATURE_MAP（opCode 2，空 params）——固件声明支持哪些 feature 位
        capture.BeginPhase(0);
        capture.Log($"── 实验 1：GET_TARGET_FEATURE_MAP（opCode 0x02，窗口 {Eq2ProbeWindow.TotalSeconds:0} 秒）──");
        Packet featureMapRequest = new(OpCode.GetTargetFeatureMap);
        Packet? featureMapResponse = await SendAndListenAsync(
            client, capture, "GET_TARGET_FEATURE_MAP", featureMapRequest, Eq2ProbeWindow).ConfigureAwait(false);
        ProbeVerdict featureMapVerdict = featureMapResponse is null
            ? ProbeVerdict.Miss("无响应（超时）", "固件不回应 feature map 查询。")
            : ProbeVerdict.Pass("有回包", RcspProbe.DescribeFeatureMap(featureMapResponse.Params));
        capture.Log($"判定: {featureMapVerdict.Judgment}");
        capture.Log($"解读: {featureMapVerdict.Interpretation}");
        steps.Add(new Eq2Step("1 GET_TARGET_FEATURE_MAP", PhaseStats(capture, "1 feature map", featureMapResponse), null, featureMapVerdict));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 2：GET_TARGET_INFO（opCode 3）——固件/协议版本上下文（对照 H3S 固件 30 03 00 01 08）
        capture.BeginPhase(0);
        capture.Log($"── 实验 2：GET_TARGET_INFO（opCode 0x03，窗口 {Eq2InfoWindow.TotalSeconds:0} 秒）──");
        Packet targetInfoRequest = new(OpCode.GetTargetInfo);
        Packet? targetInfoResponse = await SendAndListenAsync(
            client, capture, "GET_TARGET_INFO", targetInfoRequest, Eq2InfoWindow).ConfigureAwait(false);
        string targetInfoText = targetInfoResponse is null
            ? "无响应（超时）。"
            : $"params({targetInfoResponse.Params.Length}B) = {Hex(targetInfoResponse.Params)}；" + DescribeTlvInline(targetInfoResponse.Params);
        capture.Log($"解读: {targetInfoText}");
        steps.Add(new Eq2Step("2 GET_TARGET_INFO", PhaseStats(capture, "2 target info", targetInfoResponse), null,
            new ProbeVerdict(targetInfoResponse is not null, targetInfoResponse is not null ? "有回包" : "无响应", targetInfoText)));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 3：EQ 读变体（Quicky 调研：Request Data 家族里除 0x22 外的 EQ 命令，各等 3 秒）
        var eqVariantCmds = new (byte Cmd, string Note)[]
        {
            (RcspProbe.EqV1Cmd, "EQ v1（6B/段，Quicky 文档响应含 eqType+masterGain）"),
            (RcspProbe.EqLeftCmd, "左通道 EQ（v2 格式）"),
            (RcspProbe.EqRightCmd, "右通道 EQ（v2 格式）"),
            (RcspProbe.MaxEqCountCmd, "自定义 EQ 槽位数（1 字节计数，EQ 子系统存在性探针）"),
            (RcspProbe.AdaptiveEqCmd, "自适应 EQ 状态（1 字节开关）"),
        };
        foreach ((byte eqCmd, string note) in eqVariantCmds)
        {
            var (stats, verdict) = await ProbeRequestDataAsync(
                client, capture, $"实验 3.{eqCmd:X2} {RcspProbe.EqFamilyName(eqCmd)}", eqCmd, note).ConfigureAwait(false);
            steps.Add(new Eq2Step($"3 FE 01 {eqCmd:X2}（{RcspProbe.EqFamilyName(eqCmd)}）", stats, eqCmd, verdict));
            await EqProbeGapAsync(capture).ConfigureAwait(false);
        }

        // 实验 4：GET_SYS_INFO（opCode 7）单属性探针——第一轮组合掩码 0x001810 只回 function 回声 07，
        // 这里拆开验证"掩码整体被拒"还是"单属性被门控"；1<<12 = EqPresetValue 即"当前预设 ID"查询。
        var sysInfoProbes = new (uint Mask, string Note)[]
        {
            (1u << SysAttr.EqPresetValue, "仅 EqPresetValue（当前预设 ID）"),
            (1u << SysAttr.Eq, "仅 EQ 属性"),
            ((1u << SysAttr.Battery) | (1u << SysAttr.EqPresetValue), "电量锚点 + EqPresetValue（对照：锚点属性出不出）"),
        };
        foreach ((uint mask, string note) in sysInfoProbes)
        {
            capture.BeginPhase(0);
            capture.Log($"── 实验 4：GET_SYS_INFO function=0x{SysFunc.Eq:X2} mask=0x{mask:X8}（{note}，窗口 {Eq2ProbeWindow.TotalSeconds:0} 秒）──");
            Packet sysInfoRequest = new(OpCode.GetSysInfo, parameters:
            [
                SysFunc.Eq,
                (byte)(mask >> 24),
                (byte)(mask >> 16),
                (byte)(mask >> 8),
                (byte)mask,
            ]);
            Packet? sysInfoResponse = await SendAndListenAsync(
                client, capture, $"GET_SYS_INFO mask=0x{mask:X8}", sysInfoRequest, Eq2ProbeWindow).ConfigureAwait(false);
            ProbeVerdict sysInfoVerdict = sysInfoResponse is null
                ? ProbeVerdict.Miss("无响应（超时）", "该掩码下固件不应答。")
                : ProbeVerdict.Pass("有回包", RcspProbe.DescribeSysInfoPayload(sysInfoResponse.Params));
            capture.Log($"判定: {sysInfoVerdict.Judgment}");
            capture.Log($"解读: {sysInfoVerdict.Interpretation}");
            steps.Add(new Eq2Step($"4 GET_SYS_INFO mask=0x{mask:X8}", PhaseStats(capture, $"4 mask {mask:X8}", sysInfoResponse), null, sysInfoVerdict));
            await EqProbeGapAsync(capture).ConfigureAwait(false);
        }

        // 实验 5：GATT 特征枚举 + 直读（Quicky/menubar 路线：EQ/版本/电量可绕过 RCSP 直接 GATT 读）
        await ProbeGattCharacteristicsAsync(transport, capture, steps).ConfigureAwait(false);
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 实验 6：FE 01 01..0x63 全 ID 可读性扫描（qcy-h3s-windows 方法论，1.2s/ID）
        for (byte cmd = 0x01; cmd <= RcspProbe.SweepMaxCmdId; cmd++)
        {
            var (stats, verdict) = await ProbeRequestDataAsync(
                client, capture, $"实验 6 扫描 FE 01 {cmd:X2}", cmd, note: null, Eq2SweepWindow).ConfigureAwait(false);
            capture.Log($"[扫描] 0x{cmd:X2}: {verdict.Judgment}");
            steps.Add(new Eq2Step($"6 扫描 FE 01 {cmd:X2}", stats, cmd, verdict));
        }

        byte[] leftover = client.SnapshotRxBuffer();
        if (leftover.Length > 0)
        {
            capture.Log($"注意: 接收缓冲滞留 {leftover.Length}B（半截帧，后续分片未到齐）: {Hex(leftover)}");
        }

        // ── 总结 ──
        capture.Log("── eqprobe2 总结 ──");
        var eqWinners = new List<string>();
        var dataWinners = new List<string>();
        foreach (Eq2Step step in steps)
        {
            if (!step.Verdict.Hit)
            {
                continue;
            }
            dataWinners.Add(step.Label);
            bool isEqData = (step.Cmd.HasValue && RcspProbe.EqFamilyCmdIds.Contains(step.Cmd.Value))
                || step.Label.Contains("000B", StringComparison.Ordinal);
            if (isEqData)
            {
                eqWinners.Add(step.Label);
            }
        }

        foreach (Eq2Step step in steps.Where(step => !step.Label.Contains("扫描 FE 01", StringComparison.Ordinal)))
        {
            capture.Log($"  {step.Label}: {step.Verdict.Judgment}");
        }
        int sweepHits = steps.Count(step => step.Cmd.HasValue && step.Verdict.Hit && step.Label.StartsWith("6 ", StringComparison.Ordinal));
        capture.Log($"  实验 6 全扫描: FE 01 01..FE 01 {RcspProbe.SweepMaxCmdId:X2} 中有数据回包的 command 共 {sweepHits} 个" +
                    $"（0x17 降噪读为已知阳性对照；0x22 为第一轮已证空的基线）。");

        capture.Log(eqWinners.Count > 0
            ? $"结论: 拿到 EQ 数据的步骤 —— {string.Join("、", eqWinners)}。"
            : "结论: 所有 EQ 读法（含 Quicky 变体、预设 ID 查询、GATT 直读）均未拿到 EQ 数据。");
        capture.Log(dataWinners.Count > 0
            ? $"有数据回包的步骤共 {dataWinners.Count} 个 —— {string.Join("、", dataWinners)}"
            : "没有任何步骤拿到非空数据——CUSTOM 读通道在本固件上整体不可读。");

        if (eqWinners.Count == 0)
        {
            capture.Log("降级建议（UI 层）: 该批次固件大概率不支持 EQ 读（参考项目 qcy-menubar 在同协议 H3 上" +
                        "同样只能写；qcy-h3s-windows 证明同家族 H3S 可读而本机表现为「status=0 空载荷」而非超时）。" +
                        "采用「只写不读 + 乐观显示」：写入后直接把 UI 状态置为已写入值，重连时回退为本地记忆的上次写入值。");
        }

        return ExitOk;
    }

    /// <summary>
    /// 发一条 CUSTOM「Request Data」读（FE 01 cmd）并监听满整个接收窗口；打印 TX 帧与
    /// 判定/解读，返回统计与判定。全扫描传 <see cref="Eq2SweepWindow"/>（1.2s，无步间隔），
    /// 变体实验默认 <see cref="Eq2ProbeWindow"/>（3s）。
    /// </summary>
    private static async Task<(ProbeStats Stats, ProbeVerdict Verdict)> ProbeRequestDataAsync(
        H3Client client, DebugCapture capture, string label, byte cmdId, string? note, TimeSpan? window = null)
    {
        TimeSpan listen = window ?? Eq2ProbeWindow;
        capture.BeginPhase(0);
        capture.Log($"── {label}（{RcspProbe.EqFamilyName(cmdId)}" +
                    $"{(note is null ? string.Empty : $"；{note}")}，窗口 {listen.TotalSeconds:0.#} 秒）──");

        var request = new Packet(OpCode.Custom, parameters: [0xFE, 0x01, cmdId]);
        Packet? response = await SendAndListenAsync(client, capture, label, request, listen).ConfigureAwait(false);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead(request.Params, response);
        capture.Log($"判定: {verdict.Judgment}");
        capture.Log($"解读: {verdict.Interpretation}");
        return (PhaseStats(capture, label, response), verdict);
    }

    /// <summary>显式取 sn、打印完整 TX 帧并发出，等满 <paramref name="timeout"/> 窗口后取回应答。</summary>
    private static async Task<Packet?> SendAndListenAsync(
        H3Client client, DebugCapture capture, string label, Packet packet, TimeSpan timeout)
    {
        packet.Sn = client.NextSn();
        byte[] txFrame = packet.Encode();
        capture.Log($"TX {txFrame.Length}B: {Hex(txFrame)}   ← {label}" +
                    $"（opCode=0x{packet.OpCode:X2} params={Hex(packet.Params)}，超时 {timeout.TotalSeconds:0.#} 秒）");
        try
        {
            Task<Packet?> pending = client.SendAsync(packet, timeout);
            await Task.Delay(timeout).ConfigureAwait(false);
            return await pending.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            capture.Log($"发送失败: {exception.Message}");
            return null;
        }
    }

    /// <summary>结束当前接收阶段并生成统计快照（EndPhase 会重置阶段计数器，本方法只是给快照起个名）。</summary>
    private static ProbeStats PhaseStats(DebugCapture capture, string label, Packet? response)
    {
        return capture.EndPhase(label, response?.Params ?? [], response);
    }

    /// <summary>
    /// 实验 5：GATT 特征枚举 + 直读。枚举 A002 服务全部特征并打印属性；随后对带 READ
    /// 属性的特征逐个直读（000B EQ 优先、上限 12 个防长时间卡顿，单次读取超时 4 秒），
    /// EQ 特征的字节按 Quicky 直写格式解读并计入 EQ 命中。
    /// </summary>
    private static async Task ProbeGattCharacteristicsAsync(
        BleTransport transport, DebugCapture capture, List<Eq2Step> steps)
    {
        capture.BeginPhase(0);
        capture.Log("── 实验 5：GATT 特征枚举 + 直读（Quicky/menubar 路线，绕过 RCSP 通道）──");
        IReadOnlyList<GattCharacteristicInfo> characteristics;
        try
        {
            characteristics = await transport.EnumerateCharacteristicsAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            capture.Log($"GATT 特征枚举失败: {exception.Message}");
            steps.Add(new Eq2Step("5 GATT 直读", null, null, ProbeVerdict.Miss("枚举失败", exception.Message)));
            return;
        }

        capture.Log($"A002 服务共 {characteristics.Count} 个特征：");
        foreach (GattCharacteristicInfo info in characteristics)
        {
            capture.Log($"  {info.Uuid}  [{info.PropertiesText}]");
        }

        // 直读顺序：EQ 特征优先，其余可读特征按 UUID 排序，整体上限 12 个。
        var readable = characteristics
            .Where(c => c.CanRead)
            .OrderBy(c => c.Uuid == EqCharacteristicUuid ? 0 : 1)
            .ThenBy(c => c.Uuid)
            .Take(12)
            .ToList();
        if (readable.Count == 0)
        {
            capture.Log("没有任何带 READ 属性的特征——GATT 直读路线在该固件上不存在。");
            steps.Add(new Eq2Step("5 GATT 直读", null, null,
                ProbeVerdict.Miss("无 READ 特征", "A002 服务内没有可直读的特征。")));
            return;
        }

        bool eqRead = false;
        foreach (GattCharacteristicInfo info in readable)
        {
            byte[]? value;
            try
            {
                value = await transport.ReadCharacteristicAsync(info.Uuid)
                    .WaitAsync(Eq2GattReadTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                capture.Log($"  直读 {info.Uuid} → 失败: {exception.Message}");
                continue;
            }
            if (value is null)
            {
                capture.Log($"  直读 {info.Uuid} → 失败（不可读/无响应）。");
                continue;
            }
            capture.Log($"  直读 {info.Uuid} → {value.Length}B: {Hex(value)}");
            if (info.Uuid == EqCharacteristicUuid)
            {
                eqRead = value.Length >= EqProbeMinParamsLength;
                capture.Log($"    EQ 特征解读: {RcspProbe.DescribeDirectEqBytes(value)}");
            }
        }

        steps.Add(new Eq2Step("5 GATT 直读（000B）", null, null, eqRead
            ? ProbeVerdict.Pass("命中（000B 直读拿到 EQ 字节）", "Quicky 直读路线有效。")
            : ProbeVerdict.Miss("000B 直读未拿到 EQ 字节", "EQ 特征不存在/不可读/返回过短。")));
    }

    /// <summary>Packet.params 的 TLV 概览（GET_TARGET_INFO 回包解读用）。</summary>
    private static string DescribeTlvInline(ReadOnlySpan<byte> data)
    {
        var entries = TlvParser.Parse(data);
        if (entries.Count == 0)
        {
            return "无合法 TLV 项。";
        }
        var parts = entries.Select(e =>
        {
            string typeName = AdvType.Names.TryGetValue(e.Type, out string? name) ? name : "?";
            return $"type=0x{e.Type:X2}({typeName}) len={e.Value.Length + 1} value={Hex(e.Value)}";
        });
        return "TLV: " + string.Join(' ', parts);
    }

    // ───────────────────────── eqprobe3（EQ v1 读写验证） ─────────────────────────

    private static readonly TimeSpan Eq3ReadWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Eq3WriteWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// EQ v1 读写验证（真机实验通道，2026-10）。背景：本机固件对 EQ v2 读（FE 01 22）回
    /// 空载荷，但对 v1 读（FE 01 20）稳定回 47 字节（7 段，Quicky 格式：eqType + masterGain +
    /// 6B/段 freq/gain/Q）。v1 写是否生效待本命令实机判定。连接握手后依次执行：
    /// <list type="number">
    /// <item>读 v1 基线并打印 7 段曲线；失败则中止（未做任何写操作）；</item>
    /// <item>写 v1 全零平线（eqIndex/Q 沿用基线，masterGain=0、7 段 gain=0，听感平直无染色）；</item>
    /// <item>等 1 秒再读 v1：全零 → 写生效实锤；与基线一致 → 写未生效；其余 → 部分生效；</item>
    /// <item>恢复基线：把第 1 步读到的曲线原样写回（解析→构建逐字节还原，耳机最终状态与实验前一致）；</item>
    /// <item>再读 v1 验证最终状态。</item>
    /// </list>
    /// 最后打印四步曲线对照表（①基线 ②写入计划 ③写后读回 ⑤恢复后读回，④恢复写入=①）与结论。
    /// 每步打印 TX/RX 原始帧；除全零平线与基线恢复外不写任何曲线，不动降噪/其它设置。
    /// </summary>
    private static async Task<int> CmdEqProbe3Async(string targetArgument)
    {
        (ulong Address, string Name)? target = await ResolveTargetAsync(targetArgument).ConfigureAwait(false);
        if (target is null)
        {
            return ExitError;
        }
        (ulong address, string name) = target.Value;

        var capture = new DebugCapture();
        using var transport = new BleTransport();
        transport.DataReceived += capture.OnRawChunk;   // 先订原始块：每块先打印、再进协议解析
        using var client = new H3Client(transport);
        client.PacketTraced += capture.OnFrameTraced;

        Console.WriteLine($"── eqprobe3 EQ v1 读写验证：{name}（{BleScanResult.FormatAddress(address)}）──");
        try
        {
            await transport.ConnectAsync(address).WaitAsync(ConnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"连接超时（{ConnectTimeout.TotalSeconds:0} 秒）：设备可能已关机、入盒或不广播。");
            return ExitError;
        }
        capture.Log("GATT 已连接（服务 A002，写特征 0001，通知特征 0002）");

        Packet? handshake = await client.HandshakeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (handshake is null)
        {
            capture.Log("握手（NOTIFY_COMMUNICATION_WAY 00 00）2 秒无响应——无法继续实验，请确认设备未被其它 App 占用。");
            return ExitError;
        }
        capture.Log($"握手成功: {DescribePacket(handshake)}");
        capture.Log("安全声明：唯一写操作为步骤 2 的 v1 全零平线（masterGain=0、7 段增益全 0）与步骤 4 的基线原样恢复；不写任何非零实验曲线，不动降噪/其它设置。");

        // 步骤 1：读 v1 基线（后续写入与恢复都以它为准）
        capture.BeginPhase(0);
        capture.Log($"── 步骤 1：读 EQ v1 基线（FE 01 {EqV1.Cmd:X2}，窗口 {Eq3ReadWindow.TotalSeconds:0} 秒）──");
        EqV1Info? baseline = await ReadEqV1Async(client, capture, "EQ v1 读·基线").ConfigureAwait(false);
        if (baseline is null)
        {
            capture.Log("结论: 基线读取失败（无响应 / 首字节非 0x20 / 数据不足 7 段）——未执行任何写操作，耳机状态未改变。");
            return ExitError;
        }
        capture.Log(FormatEqV1Curve("①基线", baseline));
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 2：写 v1 全零平线（eqIndex 与 Q 沿用基线，仅 masterGain 与各段增益归零）
        var flatBands = baseline.Bands.Select(band => band with { Gain = 0 }).ToList();
        byte[] flatPayload = EqV1.BuildWritePayload(baseline.EqType, masterGain: 0, flatBands);
        capture.BeginPhase(0);
        capture.Log($"── 步骤 2：写 EQ v1 全零平线（eqIndex=0x{baseline.EqType:X2} 沿用基线，masterGain=0，{flatBands.Count} 段 gain=0，Q 沿用基线）──");
        Task<Packet?> flatWriteTask = StartTracedSend(client, capture, "EQ v1 写·全零平线",
            new Packet(OpCode.Custom, parameters: flatPayload), Eq3WriteWindow);
        await Task.Delay(EqProbeStepGap).ConfigureAwait(false);
        Packet? flatAck = await flatWriteTask.ConfigureAwait(false);
        capture.Log($"写入结果: {(flatAck is null ? "无 ack（写入是否生效由步骤 3 读回判定）" : DescribePacket(flatAck))}");
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 3：等 1 秒后读回，对比写入是否生效
        capture.BeginPhase(0);
        capture.Log($"── 步骤 3：读 EQ v1 对比（写入 → 等 {EqProbeStepGap.TotalSeconds:0} 秒 → 读，窗口 {Eq3ReadWindow.TotalSeconds:0} 秒）──");
        EqV1Info? afterFlat = await ReadEqV1Async(client, capture, "EQ v1 读·写后").ConfigureAwait(false);
        if (afterFlat is not null)
        {
            capture.Log(FormatEqV1Curve("③写后读回", afterFlat));
        }
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 4：恢复基线（原样写回步骤 1 读到的曲线；BuildWritePayload 对基线的重建已被
        // 单测证明与真机原始字节逐字节一致，保证"原样"）
        byte[] restorePayload = EqV1.BuildWritePayload(baseline.EqType, baseline.MasterGain, baseline.Bands);
        capture.BeginPhase(0);
        capture.Log($"── 步骤 4：恢复基线（原样写回步骤 1 的曲线，载荷 {restorePayload.Length}B）──");
        Task<Packet?> restoreWriteTask = StartTracedSend(client, capture, "EQ v1 写·恢复基线",
            new Packet(OpCode.Custom, parameters: restorePayload), Eq3WriteWindow);
        await Task.Delay(EqProbeStepGap).ConfigureAwait(false);
        Packet? restoreAck = await restoreWriteTask.ConfigureAwait(false);
        capture.Log($"恢复写入结果: {(restoreAck is null ? "无 ack（最终状态由步骤 5 读回判定）" : DescribePacket(restoreAck))}");
        await EqProbeGapAsync(capture).ConfigureAwait(false);

        // 步骤 5（验证）：读回确认最终状态
        capture.BeginPhase(0);
        capture.Log($"── 步骤 5（验证）：读 EQ v1 确认最终状态（窗口 {Eq3ReadWindow.TotalSeconds:0} 秒）──");
        EqV1Info? afterRestore = await ReadEqV1Async(client, capture, "EQ v1 读·恢复后").ConfigureAwait(false);
        if (afterRestore is not null)
        {
            capture.Log(FormatEqV1Curve("⑤恢复后读回", afterRestore));
        }

        byte[] leftover = client.SnapshotRxBuffer();
        if (leftover.Length > 0)
        {
            capture.Log($"注意: 接收缓冲滞留 {leftover.Length}B（半截帧，后续分片未到齐）: {Hex(leftover)}");
        }

        PrintEq3ComparisonTable(capture, baseline, flatPayload, afterFlat, afterRestore);
        LogEq3Conclusion(capture, baseline, afterFlat, afterRestore);
        return ExitOk;
    }

    /// <summary>发 FE 01 20 读、等满接收窗口并按 v1 解析（与 H3Client.GetEqV1Async 同参同解析）。</summary>
    private static async Task<EqV1Info?> ReadEqV1Async(H3Client client, DebugCapture capture, string label)
    {
        var request = new Packet(OpCode.Custom, parameters: [0xFE, 0x01, EqV1.Cmd]);
        Packet? response = await SendAndListenAsync(client, capture, label, request, Eq3ReadWindow).ConfigureAwait(false);
        EqV1Info? info = response is null ? null : EqV1.ParseResponse(response.Params);
        capture.Log(info is null
            ? "解析: 未拿到合法 v1 表（无响应 / 首字节非 0x20 / 数据不足 7 段）"
            : $"解析: eqType=0x{info.EqType:X2}，masterGain={info.MasterGain / 100.0:F2} dB，{info.Bands.Count} 段");
        return info;
    }

    /// <summary>一条 v1 曲线的人类可读单行打印。</summary>
    private static string FormatEqV1Curve(string title, EqV1Info info)
    {
        var sb = new StringBuilder($"曲线[{title}] eqType=0x{info.EqType:X2} masterGain={info.MasterGain / 100.0:F2} dB：");
        foreach (EqV1Band band in info.Bands)
        {
            sb.Append($"  {band.Frequency}Hz={band.Gain / 100.0:F2}dB(Q{band.Q / 100.0:F2})");
        }
        return sb.ToString();
    }

    /// <summary>两条 v1 曲线是否一致：eqType/masterGain 相同且各段 freq/gain/Q 按频率对齐后全等。</summary>
    private static bool EqV1SameCurve(EqV1Info a, EqV1Info b)
    {
        return a.EqType == b.EqType
            && a.MasterGain == b.MasterGain
            && a.Bands.Count == b.Bands.Count
            && a.Bands.All(band => b.Bands.FirstOrDefault(other => other.Frequency == band.Frequency) is EqV1Band match
                && match.Gain == band.Gain && match.Q == band.Q);
    }

    /// <summary>
    /// 四步曲线对照表：行 = masterGain / eqType / 各频段，列 = ①基线 ②写入计划 ③写后读回
    /// ⑤恢复后读回（④恢复写入与 ① 相同，不在表内重复）。读回缺失显示「无数据」。
    /// </summary>
    private static void PrintEq3ComparisonTable(
        DebugCapture capture, EqV1Info baseline, byte[] flatPayload, EqV1Info? afterFlat, EqV1Info? afterRestore)
    {
        EqV1Info? flatTarget = EqV1.ParseResponse(flatPayload);   // 复用解析器展示"写入的计划值"

        static string GainText(EqV1Info? info)
        {
            return info is null ? "无数据" : $"{info.MasterGain / 100.0:F2} dB";
        }

        static string BandText(EqV1Info? info, ushort frequency)
        {
            if (info is null)
            {
                return "无数据";
            }
            return info.Bands.FirstOrDefault(band => band.Frequency == frequency) is EqV1Band band
                ? $"{band.Gain / 100.0:F2} dB Q{band.Q / 100.0:F2}"
                : "无此段";
        }

        const int width = 16;
        capture.Log("── 四步曲线对照表（④恢复写入 = ①基线，表中略）──");
        capture.Log($"  {"字段/频段",-10} | {"① 基线",width} | {"② 写入(计划)",width} | {"③ 写后读回",width} | {"⑤ 恢复后读回",width}");
        capture.Log($"  {"masterGain",-10} | {GainText(baseline),width} | {GainText(flatTarget),width} | {GainText(afterFlat),width} | {GainText(afterRestore),width}");
        capture.Log($"  {"eqType",-10} | {$"0x{baseline.EqType:X2}",width} | {(flatTarget is null ? "无数据" : $"0x{flatTarget.EqType:X2}"),width} | " +
                    $"{(afterFlat is null ? "无数据" : $"0x{afterFlat.EqType:X2}"),width} | {(afterRestore is null ? "无数据" : $"0x{afterRestore.EqType:X2}"),width}");
        foreach (EqV1Band band in baseline.Bands)
        {
            string name = $"{band.Frequency} Hz";
            capture.Log($"  {name,-10} | {BandText(baseline, band.Frequency),width} | {BandText(flatTarget, band.Frequency),width} | " +
                        $"{BandText(afterFlat, band.Frequency),width} | {BandText(afterRestore, band.Frequency),width}");
        }
    }

    /// <summary>eqprobe3 结论：v1 写是否生效 + 耳机最终状态是否恢复。</summary>
    private static void LogEq3Conclusion(DebugCapture capture, EqV1Info baseline, EqV1Info? afterFlat, EqV1Info? afterRestore)
    {
        capture.Log("── eqprobe3 结论 ──");
        if (afterFlat is null)
        {
            capture.Log("v1 写验证: 写后读无响应/不合法，无法判定写入是否生效（请对照上方各步原始帧）。");
        }
        else if (afterFlat.MasterGain == 0 && afterFlat.Bands.All(band => band.Gain == 0))
        {
            capture.Log("v1 写验证: 写后读回 masterGain=0 且各段增益全 0，与写入值一致 —— ** v1 写生效实锤 **。");
        }
        else if (EqV1SameCurve(afterFlat, baseline))
        {
            capture.Log("v1 写验证: 写后读回与基线完全一致 —— v1 写未生效（固件忽略 cmd 0x20 写，或读回的是缓存旧值）。");
        }
        else
        {
            capture.Log("v1 写验证: 写后读回既非全零也非基线 —— 写入部分生效或设备自行改写，请对照对照表逐段分析。");
        }

        if (afterRestore is null)
        {
            capture.Log("最终状态: 恢复后读无响应 —— 步骤 4 已原样写回基线，但未能读回验证，建议用手机 App 复核 EQ。");
        }
        else if (EqV1SameCurve(afterRestore, baseline))
        {
            capture.Log("最终状态: 恢复后读回与基线完全一致 —— 耳机已恢复实验前曲线。");
        }
        else
        {
            capture.Log("最终状态: 警告 —— 恢复后读回与基线不一致，建议用手机 App 复核 EQ。");
        }
    }

    // ───────────────────────── 目标解析与配置 ─────────────────────────

    /// <summary>
    /// 把 "AA:BB:CC:DD:EE:FF"（分隔符可省略）解析成地址；否则按名称扫描匹配
    /// （先精确、后前缀）。失败返回 null 并已输出错误。
    /// </summary>
    private static async Task<(ulong Address, string Name)?> ResolveTargetAsync(string argument)
    {
        if (BleScanResult.TryParseAddress(argument, out ulong parsed))
        {
            return (parsed, argument);
        }

        Console.WriteLine($"正在按名称搜索「{argument}」（约 {TargetSearchTimeout.TotalSeconds:0} 秒）…");
        using var scanner = new BleScanner();
        List<BleScanResult> devices = await scanner.ScanAsync(TargetSearchTimeout).ConfigureAwait(false);
        BleScanResult? match = devices.FirstOrDefault(device =>
                string.Equals(device.Name, argument, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(device =>
                device.Name.StartsWith(argument, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Console.Error.WriteLine(
                $"未找到名称为「{argument}」的设备（本轮扫描到 {devices.Count} 台 BLE 设备）。" +
                "请确认耳机已开机、在附近，或改用地址指定。");
            return null;
        }

        Console.WriteLine($"已找到：{match.AddressText} {match.Name}（{match.Rssi} dBm）");
        return (match.Address, match.Name.Length > 0 ? match.Name : argument);
    }

    private static (ulong Address, string Name)? LoadTargetConfig()
    {
        AppConfig config = AppConfig.Load();
        if (config.TargetAddress is not ulong address)
        {
            return null;
        }
        return (address, config.TargetName ?? string.Empty);
    }

    private static void SaveTargetConfig(ulong address, string name)
    {
        try
        {
            AppConfig config = AppConfig.Load();
            config.TargetAddress = address;
            config.TargetName = name;
            config.Save();
            Console.WriteLine($"已保存默认目标到 {AppConfig.DefaultPath}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"保存配置失败：{exception.Message}");
        }
    }

    // ───────────────────────── 输出辅助 ─────────────────────────

    private static bool IsQcyCandidate(BleScanResult device)
        => device.HasQcyVendorData
            || device.Name.StartsWith(ConnectionManager.FallbackNamePrefix, StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("QCY", StringComparison.OrdinalIgnoreCase);

    private static string FormatDeviceLine(BleScanResult device)
        => $"  {device.AddressText}  {(device.Name.Length > 0 ? device.Name : "(无名)")}  {device.Rssi} dBm{(device.HasQcyVendorData ? "  [QCY 厂商数据]" : string.Empty)}";

    private static int PrintUsage()
    {
        Console.WriteLine(UsageText());
        return ExitOk;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        return ExitUsage;
    }

    private static string UsageText() => """
        QcyH3Tray.Cli — QCY H3 命令行工具（阶段 2 真机自测通道）

        用法:
          QcyH3Tray.Cli scan [超时秒=6]    扫描并列出附近 BLE 设备（QCY 设备排前）
          QcyH3Tray.Cli info <地址或名称>   连接设备并读取电量/降噪模式/EQ/游戏模式
          QcyH3Tray.Cli setmode <模式> [地址或名称]
                                           切换降噪模式: 关闭|通透|降噪低|降噪中|降噪高|
                                           通勤低|通勤中|通勤高|室内低|室内中|室内高|抗风|自适应
                                           （省略地址时使用 info 上次保存的默认目标）
          QcyH3Tray.Cli probe              真机自检：检查蓝牙 + 扫描 5 秒（不连接）
          QcyH3Tray.Cli debug <地址或名称>  抓包诊断：握手后静默收 1 秒，再发降噪读
                                           FF FE 01 17 与 EQ 读 FF FE 01 22（每条后收
                                           3 秒），全量打印原始字节/解析/sn 匹配结果
          QcyH3Tray.Cli eqprobe <地址或名称>
                                           EQ 读取实验：握手后做 5 组探测（基线重试 5s /
                                           连发两次 / GET_SYS_INFO / 先写默认预设全零
                                           偏移再读 / GET_ADV_INFO 掩码试探），逐步打印
                                           原始帧，最后总结哪组拿到 EQ 数据（唯一写操作
                                           为默认预设全零偏移，听感无变化）
          QcyH3Tray.Cli eqprobe2 <地址或名称>
                                           EQ 读取二轮实验（纯读无写）：feature map /
                                           EQ 读变体（FE 01 20/46/47/44/27）/ GET_SYS_INFO
                                           单属性探针（预设 ID）/ GATT 特征枚举与直读 /
                                           FE 01 01..63 全 ID 扫描，最后总结哪步拿到 EQ
          QcyH3Tray.Cli eqprobe3 <地址或名称>
                                           EQ v1 读写验证：读 v1 基线（7 段曲线）→ 写
                                           全零平线（masterGain=0、增益全 0）→ 等 1s 读
                                           回对比 → 原样恢复基线 → 读回验证，打印四步
                                           对照表与结论（唯一写操作为全零平线与恢复）
          QcyH3Tray.Cli probe-off <地址或名称>
                                           自动关机读写验证：读 0x14 基线（时长+计时，
                                           分钟）→ 当前值原样回写 → 回读对比（唯一写
                                           操作为基线原样回写，不改任何设置）
          QcyH3Tray.Cli probe-keys <地址或名称>
                                           按键映射读写验证：GET_ADV_INFO type=02 读
                                           键表（keyNum/action/function 三元组+语义名）
                                           → 0x2B 旧通道对照 → SET_ADV_INFO 整表原样
                                           回写 → 回读对比（不改任何映射）
          QcyH3Tray.Cli probe-tone <地址或名称>
                                           提示音只读探测：提示音音量（0x1D 读）、语音
                                           名称（0x19 读）、播放命令回声探测；列出已调研
                                           的写命令（3D 播放/4B 查找耳机）但不接线
          QcyH3Tray.Cli probe-multi <地址或名称>
                                           双设备连接只读探测：CMDID_1T2（0x24 读）+
                                           GET_ADV_INFO 全掩码属性表；写命令无调研出处，
                                           不实现
          QcyH3Tray.Cli help               显示本帮助

        地址格式: AA:BB:CC:DD:EE:FF（冒号/横线可省略，按十六进制解析）
        配置文件: %APPDATA%\QcyH3Controller\config.json（info/setmode 成功后保存默认目标）
        """;
}
