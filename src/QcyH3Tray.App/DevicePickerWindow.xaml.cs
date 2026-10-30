using System.Windows;
using System.Windows.Input;
using QcyH3Tray.Core.Ble;

namespace QcyH3Tray.App;

/// <summary>
/// 首启设备选择（模态）：扫描 6 秒 → 列表（名称/地址/RSSI，名称含 QCY 或 H3 的
/// 置顶加粗）→ 双击或选中 + 确定保存目标并开始连接；取消 = 退出应用。
/// 蓝牙未开启时给出明确提示。
/// </summary>
public sealed partial class DevicePickerWindow : Window
{
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(6);

    private readonly AppState _app;
    private CancellationTokenSource? _scanCts;

    internal DevicePickerWindow(AppState app)
    {
        InitializeComponent();
        _app = app;
    }

    private async void OnScanClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_scanCts is not null)
        {
            return;   // 已在扫描
        }

        DeviceList.ItemsSource = null;
        ConfirmButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StatusTextBlock.Text = "正在扫描…（6 秒）";

        _scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var scanner = new BleScanner();
            List<BleScanResult> results = await scanner.ScanAsync(ScanDuration, _scanCts.Token).ConfigureAwait(true);

            List<DeviceItem> items = [.. results
                .Select(result => new DeviceItem(result))
                .OrderByDescending(item => item.IsPreferred)
                .ThenByDescending(item => item.Rssi)];
            DeviceList.ItemsSource = items;

            StatusTextBlock.Text = items.Count == 0
                ? "扫描完成：未发现设备。请确认耳机已开机、在附近，且没有被其它设备占用（同一时间只允许一个 BLE 控制连接）。"
                : $"扫描完成：发现 {items.Count} 台设备。双击条目，或选中后点击「确定」。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "扫描已取消。";
        }
        catch (BluetoothUnavailableException exception)
        {
            StatusTextBlock.Text = exception.Message;   // 蓝牙未开启等明确提示
            ErrorLog.Write("设备扫描失败", exception);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"扫描失败：{exception.Message}";
            ErrorLog.Write("设备扫描失败", exception);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private void OnDeviceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs eventArgs)
        => ConfirmButton.IsEnabled = DeviceList.SelectedItem is not null;

    private void OnDeviceDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (DeviceList.SelectedItem is not null)
        {
            Confirm();
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs eventArgs) => Confirm();

    private void Confirm()
    {
        if (DeviceList.SelectedItem is not DeviceItem item)
        {
            return;
        }
        _app.SetTarget(item.Address, item.Name);   // 保存配置 + 设置连接目标
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs eventArgs)
        => _scanCts?.Cancel();

    /// <summary>列表条目（名称含 QCY/H3 的设备置顶加粗）。</summary>
    private sealed class DeviceItem(BleScanResult result)
    {
        public ulong Address { get; } = result.Address;

        public string Name { get; } = result.Name;

        public short Rssi { get; } = result.Rssi;

        public string DisplayName => Name.Length > 0 ? Name : "未知设备";

        public string AddressText => BleScanResult.FormatAddress(Address);

        public string RssiText => $"{Rssi} dBm";

        public bool IsPreferred =>
            Name.Contains("QCY", StringComparison.OrdinalIgnoreCase)
            || Name.Contains("H3", StringComparison.OrdinalIgnoreCase);
    }
}
