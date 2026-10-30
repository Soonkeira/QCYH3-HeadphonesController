using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Core.Ble;

/// <summary>
/// 可连接的 BLE 传输层：<see cref="IBleLink"/>（收发 RCSP 帧）之上补充
/// 定向连接与断线通知。连接建立前调用 <see cref="IBleLink.WriteAsync"/> 会抛异常。
/// </summary>
public interface IBleTransport : IBleLink
{
    /// <summary>
    /// 连接目标地址：GATT 服务/特征发现 → 订阅通知 → 保持连接。
    /// 失败抛异常（消息可直接展示），成功后 <see cref="IBleLink.DataReceived"/> 开始收帧。
    /// </summary>
    Task ConnectAsync(ulong address, CancellationToken cancellationToken = default);

    /// <summary>已建立连接意外断开时触发（主动 <see cref="IDisposable.Dispose"/> 不触发）。</summary>
    event Action? Disconnected;
}

/// <summary>传输层工厂：每次连接尝试都要全新的实例（旧实例断开后不可复用）。</summary>
public interface IBleTransportFactory
{
    IBleTransport Create();
}

/// <summary>默认工厂：创建真实 WinRT 传输层。</summary>
public sealed class BleTransportFactory : IBleTransportFactory
{
    public IBleTransport Create() => new BleTransport();
}
