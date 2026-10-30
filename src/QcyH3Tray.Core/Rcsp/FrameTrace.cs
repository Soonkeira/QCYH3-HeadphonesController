namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// Diagnostic record for one frame extracted from the notify stream: the raw
/// reassembled frame bytes, the decode result, and the pending-request match
/// decision. Delivered via <see cref="H3Client.PacketTraced"/> (CLI <c>debug</c>
/// command); no cost beyond an allocation when nobody subscribes.
/// </summary>
/// <param name="Frame">完整重组帧（含帧头 FE DC BA 与帧尾 EF）。</param>
/// <param name="Packet">解码结果；null = 解码失败（长度非法或帧尾不符），该帧已被丢弃。</param>
/// <param name="MatchedPending">true = 该包 sn 命中一个 pending 请求，包被作为应答交付。</param>
/// <param name="PendingSns">
/// 匹配判定之后仍挂起的请求 sn 快照（用于解释"为何没匹配上"：目标 sn 不在列表中即
/// 说明设备回的 sn 与请求不一致）。
/// </param>
public sealed record FrameTrace(
    byte[] Frame,
    Packet? Packet,
    bool MatchedPending,
    IReadOnlyList<byte> PendingSns);
