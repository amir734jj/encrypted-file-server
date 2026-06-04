using Renci.SshNet;
using Shared.Contracts;
using Shared.Interfaces;

namespace Api.Services.Backend;

/// <summary>
/// Stores encrypted file blobs on a remote SFTP server via SSH.NET.
/// Each call receives per-datasource connection info.
/// </summary>
public sealed class SftpBackendStorageProvider : IBackendStorageProvider
{
    public string ProviderKey => "sftp-client";
    public BackendStorageType StorageType => BackendStorageType.SftpClient;

    public Task<(Stream stream, string storagePath)> OpenWriteAsync(
        BackendConnectionInfo connection, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var storagePath = connection.ResolveStoragePath(relativePath);
            var client = Connect(connection);

            var dir = Path.GetDirectoryName(storagePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
            {
                EnsureDirectoryExists(client, dir);
            }

            var stream = client.OpenWrite(storagePath);
            return Task.FromResult<(Stream stream, string storagePath)>((new SftpWriteStream(stream, client), storagePath));
        }
        catch (Exception exception)
        {
            return Task.FromException<(Stream stream, string storagePath)>(exception);
        }
    }

    public Task<Stream> OpenReadAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        var client = Connect(connection);
        var stream = client.OpenRead(storagePath);
        return Task.FromResult<Stream>(new SftpReadStream(stream, client));
    }

    public Task<bool> DeleteAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        using var client = Connect(connection);
        if (!client.Exists(storagePath))
        {
            return Task.FromResult(false);
        }

        client.DeleteFile(storagePath);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        using var client = Connect(connection);
        return Task.FromResult(client.Exists(storagePath));
    }

    public Task<string> RenameAsync(
        BackendConnectionInfo connection, string oldStoragePath, string newRelativePath, CancellationToken ct = default)
    {
        var newStoragePath = connection.ResolveStoragePath(newRelativePath);
        using var client = Connect(connection);

        var dir = Path.GetDirectoryName(newStoragePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureDirectoryExists(client, dir);
        }

        client.RenameFile(oldStoragePath, newStoragePath);
        return Task.FromResult(newStoragePath);
    }

    public Task<List<(string path, long size, DateTimeOffset? modified)>> ListFilesAsync(
        BackendConnectionInfo connection, CancellationToken ct = default)
    {
        using var client = Connect(connection);

        // Use the SFTP working directory (user's home) rather than BasePath,
        // because BasePath="/" would traverse the entire filesystem.
        var workingDir = client.WorkingDirectory;
        var listRoot = string.IsNullOrWhiteSpace(workingDir) || workingDir == "/"
            ? (string.IsNullOrWhiteSpace(connection.BasePath) || connection.BasePath == "/" ? "." : connection.BasePath)
            : workingDir;

        var results = new List<(string path, long size, DateTimeOffset? modified)>();
        ListFilesRecursive(client, listRoot, results);
        return Task.FromResult(results);
    }

    private static void ListFilesRecursive(SftpClient client, string path, List<(string path, long size, DateTimeOffset? modified)> results)
    {
        foreach (var item in client.ListDirectory(path))
        {
            if (item.Name == "." || item.Name == "..")
                continue;

            if (item.IsDirectory)
            {
                ListFilesRecursive(client, item.FullName, results);
            }
            else if (item.IsRegularFile)
            {
                results.Add((item.FullName, item.Length,
                    item.LastWriteTimeUtc != DateTime.MinValue ? new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero) : null));
            }
        }
    }

    private static SftpClient Connect(BackendConnectionInfo connection)
    {
        var client = new SftpClient(connection.Host, connection.Port, connection.Username, connection.Password);
        client.Connect();
        return client;
    }

    private static void EnsureDirectoryExists(SftpClient client, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += $"/{part}";
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }

    private sealed class SftpWriteStream(Stream inner, SftpClient client) : Stream
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

    private sealed class SftpReadStream(Stream inner, SftpClient client) : Stream
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
