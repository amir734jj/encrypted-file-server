namespace Api.Interfaces;

/// <summary>
/// Wraps the encryption-to-backend write pipeline so callers can stream data
/// chunk by chunk without buffering the entire file in memory.
/// </summary>
public sealed class StreamingWriteHandle(
    Stream writeStream,
    Stream backendStream,
    Func<Task>? onComplete = null)
    : IAsyncDisposable
{
    private bool _completed;

    /// <summary>The outermost stream to write plaintext data to (may include compression + encryption).</summary>
    public Stream Stream => writeStream;

    /// <summary>Flushes compression/encryption and closes the backend stream.</summary>
    public async Task CompleteAsync()
    {
        _completed = true;
        await writeStream.DisposeAsync();
        await backendStream.DisposeAsync();
        if (onComplete is not null)
        {
            await onComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await writeStream.DisposeAsync();
            await backendStream.DisposeAsync();
        }
    }
}