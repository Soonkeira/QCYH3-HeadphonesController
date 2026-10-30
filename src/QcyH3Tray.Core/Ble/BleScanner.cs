using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace QcyH3Tray.Core.Ble;

/// <summary>
/// <see cref="IBleScanner"/> 的 WinRT 实现：封装 <c>BluetoothLEAdvertisementWatcher</c>
/// （Active 扫描）。收到广播即触发 <see cref="AdvertisementReceived"/>，并按地址去重
/// 累积到快照（名称取最后一次非空值，QCY 厂商数据黏滞）。扫描异常停止时把
/// <c>BluetoothError</c> 转成可读的中文异常（含"蓝牙没开"提示）。
/// </summary>
public sealed class BleScanner : IBleScanner
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, BleScanResult> _results = [];

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothError _stoppedWithError = BluetoothError.Success;
    private bool _disposed;

    public event Action<BleScanResult>? AdvertisementReceived;

    public bool IsScanning
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                return;
            }

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active,
            };
            watcher.Received += OnReceived;
            watcher.Stopped += OnWatcherStopped;
            try
            {
                watcher.Start();
            }
            catch (Exception exception)
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnWatcherStopped;
                throw new BluetoothUnavailableException(
                    "无法启动蓝牙扫描：系统蓝牙可能未开启，请打开 Windows 蓝牙开关后再试。", exception);
            }

            _results.Clear();
            _stoppedWithError = BluetoothError.Success;
            _watcher = watcher;
        }
    }

    public void Stop()
    {
        BluetoothLEAdvertisementWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher is null)
        {
            return;
        }

        // 先退订再 Stop：自己触发的 Stopped 不会覆盖 _stoppedWithError
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnWatcherStopped;
        try
        {
            watcher.Stop();
        }
        catch
        {
            // 已停止时 Stop 可能抛异常，忽略
        }
    }

    public async Task<List<BleScanResult>> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await BluetoothAvailability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        Start();
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        Stop();

        lock (_gate)
        {
            if (_stoppedWithError != BluetoothError.Success)
            {
                throw new BluetoothUnavailableException(DescribeError(_stoppedWithError));
            }
            return [.. _results.Values];
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        bool hasQcyData = false;
        string localName = string.Empty;
        var advertisement = args.Advertisement;
        if (advertisement is not null)
        {
            localName = advertisement.LocalName ?? string.Empty;
            foreach (var section in advertisement.ManufacturerData)
            {
                if (section.CompanyId == BleScanResult.QcyCompanyId)
                {
                    hasQcyData = true;
                    break;
                }
            }
        }

        var received = new BleScanResult(args.BluetoothAddress, localName,
            args.RawSignalStrengthInDBm, hasQcyData);

        lock (_gate)
        {
            if (_results.TryGetValue(args.BluetoothAddress, out var existing))
            {
                // 同一设备：名称取最后一次非空广播，厂商数据黏滞
                received = received with
                {
                    Name = received.Name.Length > 0 ? received.Name : existing.Name,
                    HasQcyVendorData = received.HasQcyVendorData || existing.HasQcyVendorData,
                };
            }
            _results[args.BluetoothAddress] = received;
        }

        AdvertisementReceived?.Invoke(received);
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        lock (_gate)
        {
            if (_watcher == sender)
            {
                _stoppedWithError = args.Error;
            }
        }
    }

    internal static string DescribeError(BluetoothError error) => error switch
    {
        BluetoothError.RadioNotAvailable => "蓝牙不可用：系统蓝牙可能已关闭，请打开 Windows 蓝牙开关后再试。",
        BluetoothError.DisabledByPolicy => "蓝牙被系统策略禁用：请联系管理员或检查组策略设置。",
        BluetoothError.DisabledByUser => "蓝牙已被用户关闭：请打开 Windows 设置中的蓝牙开关。",
        BluetoothError.DeviceNotConnected => "蓝牙设备不可达：请检查蓝牙适配器状态后重试。",
        BluetoothError.ResourceInUse => "蓝牙资源被占用：请稍后重试。",
        _ => $"蓝牙操作失败（{error}）。若系统蓝牙未开启，请先打开蓝牙开关。",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        Stop();
    }
}
