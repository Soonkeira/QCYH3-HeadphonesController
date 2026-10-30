namespace QcyH3Tray.Core.Ble;

/// <summary>
/// 系统蓝牙不可用（适配器缺失 / 蓝牙开关未打开 / radio 访问失败）时抛出，
/// 消息为可直接展示给用户的中文提示。
/// </summary>
public class BluetoothUnavailableException : Exception
{
    public BluetoothUnavailableException(string message)
        : base(message)
    {
    }

    public BluetoothUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
