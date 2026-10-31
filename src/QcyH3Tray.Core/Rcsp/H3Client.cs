namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// QCY H3 RCSP command layer, ported from Python reference <c>h3_device.py</c>
/// (class H3Device). Handles:
/// <list type="bullet">
/// <item>sn management: 1..255 cycling, skipping 0 (<c>_next_sn</c>);</item>
/// <item>stream reassembly of fragmented notify chunks into frames (<c>_handle_notify</c>);</item>
/// <item>pending-request table matching responses by sn, with unmatched packets
/// routed to <see cref="NotificationReceived"/>;</item>
/// <item>H3 command helpers (handshake, noise modes, EQ, game mode, battery, ...).</item>
/// </list>
/// </summary>
public sealed class H3Client : IDisposable
{
    private readonly IBleLink _link;
    private readonly object _gate = new();
    private readonly List<byte> _rxBuffer = [];
    private readonly Dictionary<byte, TaskCompletionSource<Packet>> _pending = new();

    private byte _sn;

    /// <summary>Raised for decoded packets that no pending request claims (device-initiated notifications).</summary>
    public event Action<Packet>? NotificationReceived;

    /// <summary>
    /// Diagnostic event fired for <em>every</em> frame extracted from the notify
    /// stream — including frames that fail to decode. Intended for the CLI
    /// <c>debug</c> command; there is no cost when nobody subscribes. Raised on
    /// the transport callback thread, in stream order, after the packet has been
    /// dispatched to its pending request or to <see cref="NotificationReceived"/>.
    /// </summary>
    public event Action<FrameTrace>? PacketTraced;

    /// <summary>Per-request timeout applied when <see cref="SendAsync"/> is called without one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public H3Client(IBleLink link)
    {
        _link = link;
        _link.DataReceived += HandleDataReceived;
    }

    /// <summary>Next sequence number: 1..255 cycling, skipping 0 (Python <c>_next_sn</c>).</summary>
    public byte NextSn()
    {
        lock (_gate)
        {
            _sn = (byte)((_sn + 1) & 0xFF);
            if (_sn == 0)
            {
                _sn = 1;
            }
            return _sn;
        }
    }

    /// <summary>
    /// Encodes and writes <paramref name="packet"/>, assigning sn 0 automatically
    /// (Python <c>send</c>). When the packet expects a response, waits for the
    /// matching sn and returns it; returns <see langword="null"/> on timeout or
    /// when no response is expected.
    /// </summary>
    public async Task<Packet?> SendAsync(Packet packet, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (packet.Sn == 0)
        {
            packet.Sn = NextSn();
        }

        TaskCompletionSource<Packet>? completion = null;
        if (packet.HasResponse != 0)
        {
            completion = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending[packet.Sn] = completion;
            }
        }

        await _link.WriteAsync(packet.Encode(), cancellationToken).ConfigureAwait(false);

        if (completion is null)
        {
            return null;
        }

        try
        {
            return await completion.Task.WaitAsync(timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                // Only drop it if no newer request reused the same sn meanwhile.
                if (_pending.TryGetValue(packet.Sn, out var current) && ReferenceEquals(current, completion))
                {
                    _pending.Remove(packet.Sn);
                }
            }
            return null;
        }
    }

    private void HandleDataReceived(byte[] data)
    {
        List<FrameTrace> traces = [];
        List<(Packet Packet, TaskCompletionSource<Packet> Completion)> matched = [];
        List<Packet> unmatched = [];
        lock (_gate)
        {
            _rxBuffer.AddRange(data);
            bool tracing = PacketTraced is not null;
            while (true)
            {
                int idx = IndexOfHeader();
                if (idx < 0)
                {
                    _rxBuffer.Clear();
                    break;
                }
                if (idx > 0)
                {
                    _rxBuffer.RemoveRange(0, idx);
                }
                if (_rxBuffer.Count < 8)
                {
                    break;
                }

                int paramLength = (_rxBuffer[5] << 8) | _rxBuffer[6];
                int total = 7 + paramLength + 1;
                if (_rxBuffer.Count < total)
                {
                    break;
                }

                var frame = _rxBuffer.Take(total).ToArray();
                _rxBuffer.RemoveRange(0, total);

                var packet = Packet.Decode(frame);
                TaskCompletionSource<Packet>? completion = null;
                bool isMatched = false;
                if (packet is not null)
                {
                    // Matching is purely by sn (opCode-agnostic), so a DATA(1)
                    // response carrying xmOpCode is matched just like an 0xFF one.
                    isMatched = _pending.Remove(packet.Sn, out completion);
                    if (completion is not null)
                    {
                        matched.Add((packet, completion));
                    }
                    else
                    {
                        unmatched.Add(packet);
                    }
                }
                if (tracing)
                {
                    traces.Add(new FrameTrace(frame, packet, isMatched, [.. _pending.Keys]));
                }
            }
        }

        foreach (var (packet, completion) in matched)
        {
            completion.TrySetResult(packet);
        }
        foreach (var packet in unmatched)
        {
            NotificationReceived?.Invoke(packet);
        }
        foreach (var trace in traces)
        {
            PacketTraced?.Invoke(trace);
        }
    }

    /// <summary>
    /// Debug helper: copy of the reassembly buffer's current contents — the bytes
    /// of a possibly half-received frame the parser is still waiting to complete.
    /// Non-empty after a listen window means a response was cut off mid-frame.
    /// </summary>
    public byte[] SnapshotRxBuffer()
    {
        lock (_gate)
        {
            return [.. _rxBuffer];
        }
    }

    private int IndexOfHeader()
    {
        for (int i = 0; i + Packet.HeaderBytes.Length <= _rxBuffer.Count; i++)
        {
            if (_rxBuffer[i] == Packet.HeaderBytes[0]
                && _rxBuffer[i + 1] == Packet.HeaderBytes[1]
                && _rxBuffer[i + 2] == Packet.HeaderBytes[2])
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Handshake after BLE connect: NOTIFY_COMMUNICATION_WAY with params 00 00
    /// (way=0 BLE vs reconnect=0; Python <c>notify_communication_way</c>).
    /// </summary>
    public Task<Packet?> HandshakeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.NotifyCommunicationWay, parameters: [0x00, 0x00]), timeout, cancellationToken);
    }

    /// <summary>Python <c>get_target_info</c>.</summary>
    public Task<Packet?> GetTargetInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.GetTargetInfo), timeout, cancellationToken);
    }

    /// <summary>Python <c>get_target_feature_map</c>.</summary>
    public Task<Packet?> GetTargetFeatureMapAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.GetTargetFeatureMap), timeout, cancellationToken);
    }

    /// <summary>Python <c>set_anc_mode</c>: noise cancelling level 0=low, 1=medium, 2=high.</summary>
    public Task<Packet?> SetAncLevelAsync(byte level, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: [0x17, 0x03, 0x01, 0x01, (byte)(level & 0xFF)]), timeout, cancellationToken);
    }

    /// <summary>Python <c>set_mode</c>: raw 5-byte payload, e.g. one of <see cref="AncMode.All"/>.</summary>
    public Task<Packet?> SetNoiseModeAsync(byte[] modeBytes, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: modeBytes), timeout, cancellationToken);
    }

    /// <summary>Python <c>get_anc_state</c>: raw GET 0x17 response packet.</summary>
    public Task<Packet?> GetAncStateAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x17]), timeout, cancellationToken);
    }

    /// <summary>
    /// Python <c>get_mode</c> 的完整解码版：读取当前降噪模式并按
    /// <see cref="AncMode.Decode"/> 解出家族/档位/中文显示名（含通勤/室内/抗风噪场景族，
    /// 真机抓包 2026-10 证实场景组为真实固件行为）。null = 设备未应答或负载不合法。
    /// </summary>
    public async Task<AncModeInfo?> GetAncModeInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await GetAncStateAsync(timeout, cancellationToken).ConfigureAwait(false);
        return response is null ? null : AncMode.Decode(response.Params);
    }

    /// <summary>
    /// Python <c>get_mode</c>: reads the current noise mode and maps it to the legacy
    /// UI mode index via <see cref="AncMode.FromParams"/>（场景族无索引 → null）。
    /// 新代码请改用 <see cref="GetAncModeInfoAsync"/>。
    /// </summary>
    public async Task<int?> GetNoiseModeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        AncModeInfo? info = await GetAncModeInfoAsync(timeout, cancellationToken).ConfigureAwait(false);
        return AncMode.ToIndex(info);
    }

    /// <summary>
    /// Python <c>get_eq</c>: reads the current EQ curve. Returns 10 db offsets in
    /// 1/100-dB units (missing bands default to 0), or null when the device did
    /// not answer or the payload is malformed. For duplicate frequencies (the
    /// device repeats 4500 Hz) the last entry wins — the user-adjustable Q=2 copy.
    /// </summary>
    public async Task<int[]?> GetEqAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var info = await GetEqInfoAsync(timeout, cancellationToken).ConfigureAwait(false);
        return info?.Offsets;
    }

    /// <summary>
    /// EQ 读取的完整结果（阶段 2 在 <see cref="GetEqAsync"/> 基础上额外携带预设 ID，
    /// 解析守卫与 Python <c>get_eq</c> 完全一致）。null 表示设备未应答或负载不合法。
    /// </summary>
    public async Task<EqInfo?> GetEqInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x22]), timeout, cancellationToken).ConfigureAwait(false);
        if (response is null || response.Params.Length < 2 || response.Params[0] != 0x22)
        {
            return null;
        }

        int declaredLength = response.Params[1];
        int available = Math.Min(declaredLength, response.Params.Length - 2);
        // Python guards: data must hold at least the 3-byte header + 7 band records.
        if (available < 3 + 7 * EqBands.BytesPerBand)
        {
            return null;
        }

        var knownFrequencies = new HashSet<ushort>(EqBands.Frequencies);
        var bands = new Dictionary<ushort, int>();
        int entryCount = (available - 3) / EqBands.BytesPerBand;
        for (int i = 0; i < entryCount; i++)
        {
            int offset = 2 + 3 + i * EqBands.BytesPerBand;
            ushort frequency = (ushort)(response.Params[offset] | (response.Params[offset + 1] << 8));
            short dbOffset = (short)(response.Params[offset + 2] | (response.Params[offset + 3] << 8));
            if (knownFrequencies.Contains(frequency))
            {
                bands[frequency] = dbOffset; // last entry wins for duplicates
            }
        }

        if (bands.Count == 0)
        {
            return null;
        }

        var result = new int[EqBands.BandCount];
        for (int i = 0; i < EqBands.BandCount; i++)
        {
            result[i] = bands.GetValueOrDefault(EqBands.Frequencies[i], 0);
        }
        return new EqInfo(response.Params[2], result);
    }

    /// <summary>
    /// 构建 SET_EQ 写入载荷（与 Python <c>set_eq</c> 逐字节一致）：
    /// <c>22 <declaredLen> <presetId> 70 FE</c> + 10 段 × 7B（freq u16LE + gain i16LE +
    /// refGain u16LE + Q u8），共 75B。<see cref="SetEqAsync"/> 与 CLI eqprobe
    /// （先写后读实验）共用，保证写入路径只有一个构造点。
    /// </summary>
    public static byte[] BuildSetEqPayload(IReadOnlyList<int> dbOffsets, byte presetId)
    {
        var payload = new byte[2 + 3 + EqBands.BandCount * EqBands.BytesPerBand]; // 22 49 + 3 + 70 = 75
        payload[0] = 0x22;
        payload[1] = (byte)(3 + EqBands.BandCount * EqBands.BytesPerBand);        // 73
        payload[2] = (byte)(presetId & 0xFF);
        payload[3] = 0x70;
        payload[4] = 0xFE;

        for (int i = 0; i < EqBands.BandCount; i++)
        {
            int dbOffset = i < dbOffsets.Count ? dbOffsets[i] : 0;
            if (dbOffset is < short.MinValue or > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(dbOffsets), dbOffset, "EQ offset out of i16 range.");
            }

            int offset = 5 + i * EqBands.BytesPerBand;
            payload[offset] = (byte)(EqBands.Frequencies[i] & 0xFF);
            payload[offset + 1] = (byte)(EqBands.Frequencies[i] >> 8);
            payload[offset + 2] = (byte)((short)dbOffset & 0xFF);
            payload[offset + 3] = (byte)(((short)dbOffset >> 8) & 0xFF);
            payload[offset + 4] = (byte)(EqBands.Defaults[i].RefGain & 0xFF);
            payload[offset + 5] = (byte)(EqBands.Defaults[i].RefGain >> 8);
            payload[offset + 6] = EqBands.Defaults[i].Q;
        }
        return payload;
    }

    /// <summary>
    /// Python <c>set_eq</c>: writes a 10-band EQ curve. <paramref name="dbOffsets"/>
    /// holds 1/100-dB values (shorter lists are padded with 0, extra entries ignored).
    /// <paramref name="presetId"/>: 0x00=custom, 0x01=default ... 0x06=classic.
    /// </summary>
    public Task<Packet?> SetEqAsync(IReadOnlyList<int> dbOffsets, byte presetId = EqPresets.Custom,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: BuildSetEqPayload(dbOffsets, presetId)),
            timeout, cancellationToken);
    }

    /// <summary>Writes one of the built-in preset curves (<see cref="EqPresets.Offsets"/>).</summary>
    public Task<Packet?> SetEqPresetAsync(byte presetId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SetEqAsync(EqPresets.GetOffsets(presetId), presetId, timeout, cancellationToken);
    }

    /// <summary>
    /// EQ v1 读（<c>FE 01 20</c>，Quicky RequestData(0x20)）。本机固件（AA:BB:CC:DD:EE:FF）
    /// 对 v2 读 <see cref="GetEqInfoAsync"/> 回空载荷，但对 v1 稳定回 47 字节（7 段）。
    /// null = 设备未应答 / 首字节非 0x20 / 数据不足 7 段（守卫在 <see cref="EqV1.ParseResponse"/>）。
    /// </summary>
    public async Task<EqV1Info?> GetEqV1Async(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new Packet(OpCode.Custom, parameters: [0xFE, 0x01, EqV1.Cmd]), timeout, cancellationToken).ConfigureAwait(false);
        return response is null ? null : EqV1.ParseResponse(response.Params);
    }

    /// <summary>
    /// EQ v1 写（cmd 0x20，与读同 ID；Quicky SetEQV1 同格式）：
    /// <c>20 [len][eqIndex][masterGain i16LE][freq u16LE + gain i16LE + q u16LE × N]</c>，
    /// 段增益自动钳到 ±12.7 dB（<see cref="EqV1.BuildWritePayload"/>）。
    /// </summary>
    public Task<Packet?> SetEqV1Async(byte eqIndex, int masterGain, IReadOnlyList<EqV1Band> bands,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: EqV1.BuildWritePayload(eqIndex, masterGain, bands)),
            timeout, cancellationToken);
    }

    /// <summary>
    /// Python <c>get_work_mode</c>: reads WORK_MODE (game mode) via GetAdvInfo.
    /// True = game mode, False = normal, null = unknown.
    /// </summary>
    public async Task<bool?> GetWorkModeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var packet = await GetAdvInfoAsync(1u << AdvType.WorkMode, timeout, cancellationToken).ConfigureAwait(false);
        if (packet is null || packet.Params.Length == 0)
        {
            return null;
        }
        foreach (var entry in TlvParser.Parse(packet.Params))
        {
            if (entry.Type == AdvType.WorkMode && entry.Value.Length >= 1)
            {
                return entry.Value[0] == 0x02;
            }
        }
        return null;
    }

    /// <summary>Python <c>set_game_mode</c>: SET_ADV_INFO `02 05 02` (on) / `02 05 01` (off).</summary>
    public Task<Packet?> SetGameModeAsync(bool enabled, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        byte value = enabled ? (byte)0x02 : (byte)0x01;
        return SendAsync(new Packet(OpCode.SetAdvInfo, parameters: [0x02, AdvType.WorkMode, value]), timeout, cancellationToken);
    }

    /// <summary>Python <c>get_sys_info</c>: function id + 32-bit big-endian attribute mask.</summary>
    public Task<Packet?> GetSysInfoAsync(byte function, uint mask, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.GetSysInfo, parameters:
        [
            function,
            (byte)((mask >> 24) & 0xFF),
            (byte)((mask >> 16) & 0xFF),
            (byte)((mask >> 8) & 0xFF),
            (byte)(mask & 0xFF),
        ]), timeout, cancellationToken);
    }

    /// <summary>
    /// Python <c>get_adv_info</c>: 32-bit big-endian mask, bit N = <see cref="AdvType"/> N.
    /// 0x0001 = battery only, 0x0020 = work mode, 0xFFFFFFFF = all.
    /// </summary>
    public Task<Packet?> GetAdvInfoAsync(uint mask, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.GetAdvInfo, parameters:
        [
            (byte)((mask >> 24) & 0xFF),
            (byte)((mask >> 16) & 0xFF),
            (byte)((mask >> 8) & 0xFF),
            (byte)(mask & 0xFF),
        ]), timeout, cancellationToken);
    }

    /// <summary>Python <c>get_battery</c>: raw battery-quantity ADV info packet.</summary>
    public Task<Packet?> GetBatteryAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return GetAdvInfoAsync(1u << AdvType.BatteryQuantity, timeout, cancellationToken);
    }

    /// <summary>
    /// Battery percentage from the battery-quantity ADV response: TLV type 0,
    /// value[0] is the percentage (0-100). Null when absent.
    /// </summary>
    public async Task<int?> GetBatteryPercentAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var packet = await GetBatteryAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (packet is null)
        {
            return null;
        }
        foreach (var entry in TlvParser.Parse(packet.Params))
        {
            if (entry.Type == AdvType.BatteryQuantity && entry.Value.Length >= 1)
            {
                return entry.Value[0];
            }
        }
        return null;
    }

    // ───────────────────────── 阶段 4a：扩展功能（自动关机 / 按键映射 / 通用读） ─────────────────────────

    /// <summary>
    /// 自动关机（电源管理，cmd 0x14）读：CUSTOM <c>FE 01 14</c>，按 <see cref="PowerManager.ParseResponse"/>
    /// 解出「配置时长 + 当前计时」（两段 u16LE 分钟）。null = 设备未应答 / 回声不符 / 数据不足 4B。
    /// 出处：Quicky ParsePowerManager + qcy-h3s 真机与本机一致的 <c>14 04 FF FF 00 00</c>。
    /// </summary>
    public async Task<PowerManagerInfo?> GetPowerManagerAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await SendCustomReadAsync(PowerManager.Cmd, timeout, cancellationToken).ConfigureAwait(false);
        return response is null ? null : PowerManager.ParseResponse(response.Params);
    }

    /// <summary>
    /// 自动关机（cmd 0x14）写：<c>14 04 [timeLo][timeHi][curLo][curHi]</c>（u16LE 分钟；
    /// 0xFFFF = 不自动关机）。出处：Quicky NewBookingRebootCommand、qcy-h3s set_power_manager。
    /// </summary>
    public Task<Packet?> SetPowerManagerAsync(int powerOffMinutes, int currentMinutes = 0,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: PowerManager.BuildWritePayload(powerOffMinutes, currentMinutes)),
            timeout, cancellationToken);
    }

    /// <summary>
    /// 按键映射表读：GET_ADV_INFO 掩码 <c>1 &lt;&lt; 2</c>（<see cref="AdvType.KeySettings"/>），
    /// TLV type=2 值按 <see cref="KeyMap.ParseAdvInfoValue"/> 解为 [keyNum, action, function] 三元组。
    /// null = 设备未应答 / 无 type=2 TLV / 无完整三元组。0x2B 旧通道读不通（BOTOES.md 实测），勿用。
    /// </summary>
    public async Task<List<KeyMappingEntry>?> GetKeyMappingsAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var packet = await GetAdvInfoAsync(1u << AdvType.KeySettings, timeout, cancellationToken).ConfigureAwait(false);
        if (packet is null)
        {
            return null;
        }
        foreach (var entry in TlvParser.Parse(packet.Params))
        {
            if (entry.Type == AdvType.KeySettings)
            {
                return KeyMap.ParseAdvInfoValue(entry.Value);
            }
        }
        return null;
    }

    /// <summary>
    /// 按键映射表写：SET_ADV_INFO（0xC0）LTV <c>[len=1+3N][0x02][三元组 × N]</c>。
    /// <b>必须整表写入</b>（SDK 把载荷当完整配置；部分列表会清掉其余按键——BOTOES.md 实测与 APK 反编译结论）。
    /// UI 接线（阶段 4b）请先 <see cref="GetKeyMappingsAsync"/> 取整表、改一条后回写。
    /// </summary>
    public Task<Packet?> SetKeyMappingsAsync(IReadOnlyList<KeyMappingEntry> entries,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.SetAdvInfo, parameters: KeyMap.BuildSetAdvInfoPayload(entries)),
            timeout, cancellationToken);
    }

    /// <summary>
    /// 通用 CUSTOM「Request Data」读：<c>FE 01 &lt;cmd&gt;</c>，返回原始回包（含 [cmd][len][data] 载荷）。
    /// 探针命令（提示音音量 0x1D / 双设备 0x24 等）共用，sn 匹配走既有通道。
    /// </summary>
    public Task<Packet?> SendCustomReadAsync(byte cmdId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: [0xFE, 0x01, cmdId]), timeout, cancellationToken);
    }

    /// <summary>
    /// 播放提示音（CUSTOM <c>3D 01 &lt;toneID&gt;</c>，Quicky TonePlay.go）：写 + ack 判定，
    /// 无回读通道。null = 设备无应答（命令可能未生效）。R2 级外放音效，调用方须先经 UI 确认。
    /// </summary>
    public Task<Packet?> PlayToneAsync(byte toneId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: ToneCommands.BuildPlayTonePayload(toneId)),
            timeout, cancellationToken);
    }

    /// <summary>
    /// 查找耳机（CUSTOM <c>4B 01 01</c>，qcy-h3s find_earphones）：写 + ack 判定，无回读通道。
    /// null = 设备无应答。R2 级外放音效（当前音量响铃），调用方须先经 UI 确认。
    /// </summary>
    public Task<Packet?> FindEarbudsAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(new Packet(OpCode.Custom, parameters: ToneCommands.BuildFindEarbudsPayload()),
            timeout, cancellationToken);
    }

    public void Dispose()
    {
        _link.DataReceived -= HandleDataReceived;
    }
}
