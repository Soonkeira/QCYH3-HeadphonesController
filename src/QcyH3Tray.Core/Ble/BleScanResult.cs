using System.Globalization;
using System.Text;

namespace QcyH3Tray.Core.Ble;

/// <summary>
/// 一条 BLE 广播观测结果。同一设备的重复广播由扫描器合并：
/// 名称取最后一次非空值，QCY 厂商数据一旦见过即保持为真。
/// </summary>
public sealed record BleScanResult(ulong Address, string Name, short Rssi, bool HasQcyVendorData)
{
    /// <summary>QCY 厂商数据 Company ID（广播 ManufacturerData）。</summary>
    public const ushort QcyCompanyId = 0x521C;

    /// <summary>形如 AA:BB:CC:DD:EE:FF 的地址文本。</summary>
    public string AddressText => FormatAddress(Address);

    public static string FormatAddress(ulong address)
    {
        var builder = new StringBuilder(17);
        for (int i = 5; i >= 0; i--)
        {
            if (i < 5)
            {
                builder.Append(':');
            }
            builder.Append(((byte)(address >> (i * 8))).ToString("X2"));
        }
        return builder.ToString();
    }

    /// <summary>
    /// 解析用户输入的 MAC 地址："AA:BB:CC:DD:EE:FF"，分隔符（冒号/横线）可省略，
    /// 统一按 12 位十六进制解析。其余输入（如设备名）返回 false。
    /// </summary>
    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is ':' or '-')
            {
                continue;
            }
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
            builder.Append(c);
        }

        return builder.Length == 12
            && ulong.TryParse(builder.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}
