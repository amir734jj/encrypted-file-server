using Api.Data.Entities;

namespace Api.Interfaces;

public interface IFileStorageService
{
    Task<EncryptedFile> StoreFileAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType, Stream content);
    Task<StreamingWriteHandle> OpenWriteStreamAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType);
    Task<Stream> OpenDecryptedStreamAsync(EncryptedFile file);
    Task<Stream> OpenRawStreamAsync(EncryptedFile file);
    Task<bool> DeleteFileAsync(EncryptedFile file);
}

/// <summary>
/// Wraps the encryption-to-backend write pipeline so callers can stream data
/// chunk by chunk without buffering the entire file in memory.
/// </summary>
public sealed class StreamingWriteHandle : IAsyncDisposable
{
    private readonly Stream _cryptoStream;
    private readonly Stream _backendStream;
    private readonly Func<long, Task<EncryptedFile>> _finalize;
    private bool _completed;

    public StreamingWriteHandle(Stream cryptoStream, Stream backendStream, Func<long, Task<EncryptedFile>> finalize)
    {
        _cryptoStream = cryptoStream;
        _backendStream = backendStream;
        _finalize = finalize;
    }

    /// <summary>The encryption stream to write plaintext data to.</summary>
    public Stream Stream => _cryptoStream;

    /// <summary>Flushes encryption, closes backend stream, and saves the file metadata.</summary>
    public async Task<EncryptedFile> CompleteAsync(long bytesWritten = 0)
    {
        _completed = true;
        await _cryptoStream.DisposeAsync();
        await _backendStream.DisposeAsync();
        return await _finalize(bytesWritten);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _cryptoStream.DisposeAsync();
            await _backendStream.DisposeAsync();
        }
    }
}
