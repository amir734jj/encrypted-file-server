using Api.Data.Entities;

namespace Api.Interfaces;

/// <summary>
/// Wraps the encryption-to-backend write pipeline so callers can stream data
/// chunk by chunk without buffering the entire file in memory.
/// </summary>
public sealed class StreamingWriteHandle(
    Stream cryptoStream,
    Stream backendStream,
    Func<long, Task<EncryptedFile>> finalize)
    : IAsyncDisposable
{
    private bool _completed;

    /// <summary>The encryption stream to write plaintext data to.</summary>
    public Stream Stream => cryptoStream;

    /// <summary>Flushes encryption, closes backend stream, and saves the file metadata.</summary>
    public async Task<EncryptedFile> CompleteAsync(long bytesWritten = 0)
    {
        _completed = true;
        await cryptoStream.DisposeAsync();
        await backendStream.DisposeAsync();
        return await finalize(bytesWritten);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await cryptoStream.DisposeAsync();
            await backendStream.DisposeAsync();
        }
    }
}