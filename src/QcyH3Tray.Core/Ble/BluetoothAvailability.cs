using Windows.Devices.Radios;

namespace QcyH3Tray.Core.Ble;

/// <summary>
/// 蓝牙 radio 可用性检查：扫描/连接前调用，把"蓝牙没开"这类环境问题
/// 转成带明确中文提示的 <see cref="BluetoothUnavailableException"/>。
/// </summary>
public static class BluetoothAvailability
{
    /// <summary>
    /// 确认系统蓝牙 radio 存在且已开启，否则抛
    /// <see cref="BluetoothUnavailableException"/>。
    /// </summary>
    public static async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            if (bluetooth is null)
            {
                throw new BluetoothUnavailableException("未找到蓝牙适配器：本机似乎没有蓝牙硬件或驱动不可用。");
            }
            if (bluetooth.State is RadioState.Off or RadioState.Disabled)
            {
                throw new BluetoothUnavailableException("蓝牙未开启：请打开 Windows 设置中的蓝牙开关后再试。");
            }
        }
        catch (BluetoothUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BluetoothUnavailableException($"无法访问系统蓝牙状态：{exception.Message}", exception);
        }
    }
}
