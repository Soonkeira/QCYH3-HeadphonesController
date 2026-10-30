using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Core.Ble;

/// <summary>A002 服务内一个特征的元数据（eqprobe2 诊断用）。</summary>
/// <param name="Uuid">特征完整 UUID。</param>
/// <param name="Properties">特征属性标志（Read/Write/WriteWithoutResponse/Notify…）。</param>
public sealed record GattCharacteristicInfo(Guid Uuid, GattCharacteristicProperties Properties)
{
    /// <summary>是否带 READ 属性（可 GATT 直读）。</summary>
    public bool CanRead => Properties.HasFlag(GattCharacteristicProperties.Read);

    /// <summary>属性标志的紧凑文本（如 "READ | WRITE"）。</summary>
    public string PropertiesText
    {
        get
        {
            var flags = new List<string>();
            if (Properties.HasFlag(GattCharacteristicProperties.Read)) { flags.Add("READ"); }
            if (Properties.HasFlag(GattCharacteristicProperties.Write)) { flags.Add("WRITE"); }
            if (Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) { flags.Add("WRITE_NR"); }
            if (Properties.HasFlag(GattCharacteristicProperties.Notify)) { flags.Add("NOTIFY"); }
            if (Properties.HasFlag(GattCharacteristicProperties.Indicate)) { flags.Add("INDICATE"); }
            return flags.Count > 0 ? string.Join('|', flags) : Properties.ToString();
        }
    }
}

/// <summary>
/// <see cref="IBleTransport"/> 的 WinRT GATT 实现（Windows.Devices.Bluetooth）：
/// 连接 QCY H3 的 A002 服务，通过 00000001 特征（WriteWithoutResponse）发送、
/// 00000002 特征（Notify）接收。<c>GattSession.MaintainConnection = true</c> 维持链路，
/// <c>ConnectionStatusChanged</c> 断线时触发 <see cref="IBleTransport.Disconnected"/>。
/// 实例不可复用：断开/释放后请经 <see cref="IBleTransportFactory"/> 取新实例。
/// </summary>
public sealed class BleTransport : IBleTransport
{
    /// <summary>QCY H3 RCSP 服务 UUID（Python 参考实现 SERVICE_UUID）。</summary>
    public static readonly Guid ServiceUuid = new("0000a002-0000-1000-8000-00805f9b34fb");

    /// <summary>写特征 UUID（WriteWithoutResponse）。</summary>
    public static readonly Guid WriteUuid = new("00000001-0000-1000-8000-00805f9b34fb");

    /// <summary>通知特征 UUID。</summary>
    public static readonly Guid NotifyUuid = new("00000002-0000-1000-8000-00805f9b34fb");

    private readonly object _gate = new();

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private GattSession? _session;
    private bool _ready;
    private bool _disposed;

    public event Action<byte[]>? DataReceived;

    public event Action? Disconnected;

    /// <summary>系统是否视该设备为已连接。</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
        }
    }

    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken = default)
    {
        await BluetoothAvailability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is not null)
            {
                throw new InvalidOperationException("该传输层已在连接中或已用过，请使用新实例。");
            }
        }

        // 设备不广播/系统不认识该地址时返回 null
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            throw new InvalidOperationException(
                $"无法连接 {BleScanResult.FormatAddress(address)}：设备当前不可见。" +
                "请确认耳机已开机、在附近（取出充电盒），且系统蓝牙已开启。");
        }

        try
        {
            var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                throw new InvalidOperationException(
                    $"未找到 RCSP 服务（{ServiceUuid}）：{services.Status}。" +
                    "请确认该设备是 QCY H3，且未被手机等其它设备占用。");
            }

            var service = services.Services[0];
            var write = await FindCharacteristicAsync(
                service, WriteUuid, GattCharacteristicProperties.WriteWithoutResponse, cancellationToken)
                .ConfigureAwait(false);
            var notify = await FindCharacteristicAsync(
                service, NotifyUuid, GattCharacteristicProperties.Notify, cancellationToken)
                .ConfigureAwait(false);

            // MaintainConnection 让系统主动维持链路，断线能及时收到 ConnectionStatusChanged
            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)
                .AsTask(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("无法建立 GATT 会话（GattSession 为空）。");
            session.MaintainConnection = true;

            lock (_gate)
            {
                _device = device;
                _writeCharacteristic = write;
                _notifyCharacteristic = notify;
                _session = session;
            }

            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            notify.ValueChanged += OnValueChanged;

            var cccdStatus = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (cccdStatus != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"订阅耳机通知特征失败：{cccdStatus}。");
            }

            lock (_gate)
            {
                _ready = true;
            }
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        GattCharacteristic? writeCharacteristic;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            writeCharacteristic = _writeCharacteristic;
        }
        if (writeCharacteristic is null)
        {
            throw new InvalidOperationException("尚未连接耳机，无法发送数据。");
        }

        var writer = new DataWriter();
        try
        {
            writer.WriteBytes(data);
            var buffer = writer.DetachBuffer();
            var status = await writeCharacteristic
                .WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"向耳机写入失败：{status}（链路可能已断开）。");
            }
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// 枚举 A002 服务内的全部特征（含 UUID 与属性标志）。仅诊断通道使用
    /// （eqprobe2 的 GATT 直读实验），正常收发路径不依赖本方法。
    /// 失败抛异常（消息可直接展示）。
    /// </summary>
    public async Task<IReadOnlyList<GattCharacteristicInfo>> EnumerateCharacteristicsAsync(
        CancellationToken cancellationToken = default)
    {
        BluetoothLEDevice? device;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            device = _device;
        }
        if (device is null)
        {
            throw new InvalidOperationException("尚未连接耳机，无法枚举特征。");
        }

        var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            throw new InvalidOperationException($"枚举 A002 服务失败：{services.Status}。");
        }

        var characteristics = await services.Services[0]
            .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (characteristics.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"枚举 A002 特征失败：{characteristics.Status}。");
        }

        return characteristics.Characteristics
            .Select(c => new GattCharacteristicInfo(c.Uuid, c.CharacteristicProperties))
            .ToList();
    }

    /// <summary>
    /// 直读指定特征的当前值（GATT Read，无 RCSP 帧封装）。特征不存在、无读属性或
    /// 读失败（含 <see cref="GattCommunicationStatus.Unreachable"/>）返回 null——
    /// 诊断通道需要"哪些特征能读"的完整事实，而非异常中断。仅 eqprobe2 使用。
    /// </summary>
    public async Task<byte[]?> ReadCharacteristicAsync(Guid characteristicUuid, CancellationToken cancellationToken = default)
    {
        BluetoothLEDevice? device;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            device = _device;
        }
        if (device is null)
        {
            throw new InvalidOperationException("尚未连接耳机，无法直读特征。");
        }

        var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            return null;
        }

        var found = await services.Services[0]
            .GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var characteristic = found.Characteristics.FirstOrDefault(c =>
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read));
        if (found.Status != GattCommunicationStatus.Success || characteristic is null)
        {
            return null;
        }

        var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (read.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        var reader = DataReader.FromBuffer(read.Value);
        try
        {
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            return data;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>按 UUID 查找特征，优先匹配带 <paramref name="requiredProperty"/> 的那个。</summary>
    private static async Task<GattCharacteristic> FindCharacteristicAsync(
        GattDeviceService service,
        Guid characteristicUuid,
        GattCharacteristicProperties requiredProperty,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"枚举特征 {characteristicUuid} 失败：{result.Status}。");
        }

        var characteristic = result.Characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(requiredProperty))
            ?? result.Characteristics.FirstOrDefault()
            ?? throw new InvalidOperationException($"未找到特征 {characteristicUuid}，设备可能不是 QCY H3。");
        return characteristic;
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] data;
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        try
        {
            data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
        }
        finally
        {
            reader.Dispose();
        }

        if (data.Length > 0)
        {
            DataReceived?.Invoke(data);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool raise;
        lock (_gate)
        {
            raise = _ready && sender == _device && sender.ConnectionStatus != BluetoothConnectionStatus.Connected;
            if (raise)
            {
                _ready = false;
            }
        }
        if (raise)
        {
            Disconnected?.Invoke();
        }
    }

    /// <summary>释放 GATT 会话、特征订阅与设备对象（幂等）。</summary>
    private void Cleanup()
    {
        BluetoothLEDevice? device;
        GattCharacteristic? notify;
        GattSession? session;
        lock (_gate)
        {
            device = _device;
            notify = _notifyCharacteristic;
            session = _session;
            _device = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _session = null;
            _ready = false;
        }

        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }
        if (notify is not null)
        {
            notify.ValueChanged -= OnValueChanged;
        }
        session?.Dispose();
        device?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        Cleanup();
    }
}
