namespace QcyH3Tray.Core.Ble;

/// <summary>
/// BLE 广播扫描抽象：真实实现封装 <c>BluetoothLEAdvertisementWatcher</c>，
/// 测试用假实现直接投放 <see cref="BleScanResult"/>。
/// </summary>
public interface IBleScanner : IDisposable
{
    /// <summary>每收到一条广播触发一次（同一设备重复广播会多次触发）。</summary>
    event Action<BleScanResult>? AdvertisementReceived;

    /// <summary>当前是否在监视广播。</summary>
    bool IsScanning { get; }

    /// <summary>开始持续监视广播（重连监听用）。</summary>
    void Start();

    /// <summary>停止监视（幂等）。</summary>
    void Stop();

    /// <summary>一次性扫描 <paramref name="timeout"/>，返回去重后的设备列表。</summary>
    /// <exception cref="BluetoothUnavailableException">蓝牙关闭或不可用。</exception>
    Task<List<BleScanResult>> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
