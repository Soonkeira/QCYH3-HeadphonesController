namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// Transport abstraction for the RCSP command layer: writes complete frames to
/// the device and raises <see cref="DataReceived"/> for every chunk arriving on
/// the BLE notify characteristic. Phase 1 drives this with an in-memory fake;
/// phase 2 wires a real WinRT GATT implementation.
/// </summary>
public interface IBleLink : IDisposable
{
    /// <summary>Writes one complete RCSP frame to the write characteristic.</summary>
    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>Raised for every raw chunk received from the notify characteristic (may be fragmented).</summary>
    event Action<byte[]>? DataReceived;
}
