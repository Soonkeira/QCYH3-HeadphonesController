namespace QcyH3Tray.Core.Rcsp;

/// <summary>EQ 读取结果：当前预设 ID + 10 段增益（1/100-dB）。</summary>
/// <param name="PresetId">0x00=自定义，0x01=默认 … 0x06=古典（响应数据首字节）。</param>
/// <param name="Offsets">10 段增益，单位 1/100-dB，顺序同 <see cref="EqBands.Frequencies"/>。</param>
public sealed record EqInfo(byte PresetId, int[] Offsets);
