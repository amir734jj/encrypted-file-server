using FluentFTP;
using Shared.Interfaces;

namespace Api.Services.Backend;

/// <summary>
/// Stores encrypted file blobs on a remote FTP server via FluentFTP.
/// Each call receives per-datasource connection info.
/// </summary>
public sealed class FtpBackendStorageProvider(ILogger<FtpBackendStorageProvider> logger) : IBackendStorageProvider
{
    public string ProviderKey => "ftp-client";

    public async Task<(Stream stream, string storagePath)> OpenWriteAsync(
        BackendConnectionInfo connection, Guid fileId, CancellationToken ct = default)
    {
        var storagePath = $"{connection.BasePath.TrimEnd('/')}/{fileId}.enc";
        var client = await ConnectAsync(connection, ct);

        var stream = await client.OpenWrite(storagePath, token: ct);
        // Wrap in a stream that disposes the FTP client when done writing
        return (new FtpWriteStream(stream, client), storagePath);
    }

    public async Task<Stream> OpenReadAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        var client = await ConnectAsync(connection, ct);
        var stream = await client.OpenRead(storagePath, token: ct);
        return new FtpReadStream(stream, client);
    }

    public async Task<bool> DeleteAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(connection, ct);
        if (!await client.FileExists(storagePath, ct))
            return false;

        await client.DeleteFile(storagePath, ct);
        return true;
    }

    public async Task<bool> ExistsAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(connection, ct);
        return await client.FileExists(storagePath, ct);
    }

    private static async Task<AsyncFtpClient> ConnectAsync(BackendConnectionInfo connection, CancellationToken ct)
    {
        var client = new AsyncFtpClient(connection.Host, connection.Username, connection.Password, connection.Port);
        if (connection.UseSsl)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true;
        }
        await client.Connect(ct);
        return client;
    }

    /// <summary>Wraps an FTP write stream so the client is disposed after writing.</summary>
    private sealed class FtpWriteStream(Stream inner, AsyncFtpClient client) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            client.Dispose();
            await base.DisposeAsync();
        }
    }

    /// <summary>Wraps an FTP read stream so the client is disposed after reading.</summary>
    private sealed class FtpReadStream(Stream inner, AsyncFtpClient client) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            client.Dispose();
            await base.DisposeAsync();
        }
    }
}
