using QcyH3Tray.Core.Ble;

namespace QcyH3Tray.Tests;

/// <summary>假 radio 监视：测试直接驱动可用性事件（蓝牙关闭/恢复路径，plan.md D4）。</summary>
internal sealed class FakeRadioMonitor : IBluetoothRadioMonitor
{
    public event Action<bool>? AvailabilityChanged;

    public int StartCalls { get; private set; }

    public bool IsAvailable { get; private set; }

    public void Start() => StartCalls++;

    public void SetAvailable(bool available)
    {
        IsAvailable = available;
        AvailabilityChanged?.Invoke(available);
    }

    public void Dispose()
    {
    }
}
