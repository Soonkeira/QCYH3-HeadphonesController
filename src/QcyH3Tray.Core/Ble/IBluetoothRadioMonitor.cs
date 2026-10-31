using Windows.Devices.Radios;

namespace QcyH3Tray.Core.Ble;

/// <summary>
/// 蓝牙 radio 可用性监视抽象（测试注入假实现）：
/// <see cref="ConnectionManager"/> 进入蓝牙不可用状态后订阅它，radio 恢复时自动重连（plan.md D4）。
/// </summary>
public interface IBluetoothRadioMonitor : IDisposable
{
    /// <summary>当前蓝牙 radio 是否可用（存在且已开启）。监视启动前返回 false。</summary>
    bool IsAvailable { get; }

    /// <summary>可用性变化时触发（参数 = 当前是否可用）。可能在任意线程触发。</summary>
    event Action<bool>? AvailabilityChanged;

    /// <summary>开始监视（幂等）。Radio API 不可用时实现方应退化为定时探测。</summary>
    void Start();
}

/// <summary>
/// <see cref="IBluetoothRadioMonitor"/> 的 WinRT 默认实现：优先订阅
/// <c>Radio.StateChanged</c>（Windows.Devices.Radios）实时感知开关；
/// Radio API 访问失败（权限/驱动异常）时退化为每 10 秒
/// <see cref="BluetoothAvailability.EnsureAvailableAsync"/> 定时探测。
/// </summary>
public sealed class WindowsRadioMonitor : IBluetoothRadioMonitor
{
    /// <summary>轮询兜底间隔（订阅失败时使用；≥10 秒）。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private bool _started;
    private bool _available;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public event Action<bool>? AvailabilityChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_pollCts is not null && _disposed, this);
            if (_started)
            {
                return;
            }
            _started = true;
        }
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            var bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            if (bluetooth is null)
            {
                // 无蓝牙 radio：判定不可用，并退化为轮询（等待适配器出现，如 USB 蓝牙插入）
                SetAvailable(false);
                StartPolling();
                return;
            }
            bluetooth.StateChanged += OnRadioStateChanged;
            SetAvailable(bluetooth.State is not (RadioState.Off or RadioState.Disabled));
        }
        catch (Exception)
        {
            // Radio API 不可用：退化为定时探测
            StartPolling();
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
        => SetAvailable(sender.State is not (RadioState.Off or RadioState.Disabled));

    private void StartPolling()
    {
        bool started = false;
        lock (_gate)
        {
            if (!_started || _pollCts is not null)
            {
                return;
            }
            _pollCts = new CancellationTokenSource();
            started = true;
        }
        if (started)
        {
            _ = PollLoopAsync(_pollCts!.Token);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool available;
            try
            {
                await BluetoothAvailability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
                available = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                available = false;
            }
            SetAvailable(available);
        }
    }

    private void SetAvailable(bool value)
    {
        Action<bool>? handler;
        lock (_gate)
        {
            if (_available == value)
            {
                return;
            }
            _available = value;
            handler = AvailabilityChanged;
        }
        handler?.Invoke(value);
    }

    private bool _disposed;

    public void Dispose()
    {
        CancellationTokenSource? pollCts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            pollCts = _pollCts;
            _pollCts = null;
        }
        if (pollCts is not null)
        {
            try
            {
                pollCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            pollCts.Dispose();
        }
    }
}
