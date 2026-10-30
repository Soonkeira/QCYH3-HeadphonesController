using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// In-memory IBleLink fake for driving <see cref="H3Client"/> without hardware:
/// records every written frame and lets tests inject raw notify chunks.
/// </summary>
internal sealed class FakeBleLink : IBleLink
{
    public List<byte[]> WrittenFrames { get; } = [];

    public event Action<byte[]>? DataReceived;

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        WrittenFrames.Add(data);
        return Task.CompletedTask;
    }

    public void RaiseReceived(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            DataReceived?.Invoke(chunk);
        }
    }

    public void Dispose()
    {
    }
}
