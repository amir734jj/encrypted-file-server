using FluentFTP;
using Microsoft.Extensions.Logging;
using Shared.Contracts;
using Shared.Interfaces;

namespace Api.Services.Backend;

/// <summary>
/// Stores encrypted file blobs on a remote FTP server via FluentFTP.
/// Each call receives per-datasource connection info.
/// </summary>
public sealed class FtpBackendStorageProvider(ILogger<FtpBackendStorageProvider> logger) : IBackendStorageProvider
{
    public string ProviderKey => "ftp-client";
    public BackendStorageType StorageType => BackendStorageType.FtpClient;

    public async Task<(Stream stream, string storagePath)> OpenWriteAsync(
        BackendConnectionInfo connection, string relativePath, CancellationToken ct = default)
    {
        var storagePath = connection.ResolveStoragePath(relativePath);
        var client = await ConnectAsync(connection, ct);

        logger.LogInformation("FTP connected to {Host}:{Port} as {User}. Working dir: {Pwd}",
            connection.Host, connection.Port, connection.Username, await client.GetWorkingDirectory(ct));
        logger.LogInformation("FTP writing to storagePath={StoragePath}, basePath={BasePath}, relativePath={RelativePath}",
            storagePath, connection.BasePath, relativePath);

        var dir = Path.GetDirectoryName(storagePath)?.Replace('\\', '/');
        logger.LogInformation("FTP directory to ensure: {Dir}", dir);
        if (!string.IsNullOrEmpty(dir))
        {
            var dirExists = await client.DirectoryExists(dir, ct);
            logger.LogInformation("FTP directory {Dir} exists: {Exists}", dir, dirExists);
            if (!dirExists)
            {
                await client.CreateDirectory(dir, ct);
                logger.LogInformation("FTP created directory {Dir}", dir);
            }
        }

        logger.LogInformation("FTP calling OpenWrite({StoragePath})", storagePath);
        var stream = await client.OpenWrite(storagePath, token: ct);
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
        {
            return false;
        }

        await client.DeleteFile(storagePath, ct);
        return true;
    }

    public async Task<bool> ExistsAsync(
        BackendConnectionInfo connection, string storagePath, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(connection, ct);
        return await client.FileExists(storagePath, ct);
    }

    public async Task<string> RenameAsync(
        BackendConnectionInfo connection, string oldStoragePath, string newRelativePath, CancellationToken ct = default)
    {
        var newStoragePath = connection.ResolveStoragePath(newRelativePath);
        using var client = await ConnectAsync(connection, ct);

        var dir = Path.GetDirectoryName(newStoragePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(dir) && !await client.DirectoryExists(dir, ct))
        {
            await client.CreateDirectory(dir, ct);
        }

        await client.Rename(oldStoragePath, newStoragePath, ct);
        return newStoragePath;
    }

    private static async Task<AsyncFtpClient> ConnectAsync(BackendConnectionInfo connection, CancellationToken ct)
    {
        var client = new AsyncFtpClient(connection.Host, connection.Username, connection.Password, connection.Port);
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        if (connection.UseSsl)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true;
        }
        await client.Connect(ct);
        return client;
    }

    public async Task<List<(string path, long size, DateTimeOffset? modified)>> ListFilesAsync(
        BackendConnectionInfo connection, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(connection, ct);

        // Use the FTP working directory (user's home) rather than BasePath,
        // because BasePath="/" would traverse the entire filesystem.
        var workingDir = await client.GetWorkingDirectory(ct);
        var listRoot = string.IsNullOrWhiteSpace(workingDir) || workingDir == "/"
            ? (string.IsNullOrWhiteSpace(connection.BasePath) || connection.BasePath == "/" ? "." : connection.BasePath)
            : workingDir;

        logger.LogInformation("FTP ListFiles: BasePath={BasePath}, WorkingDir={Pwd}, ListRoot={ListRoot}",
            connection.BasePath, workingDir, listRoot);

        var results = new List<(string path, long size, DateTimeOffset? modified)>();
        await ListFtpRecursiveAsync(client, listRoot, results, logger, ct);

        // Make paths relative to the listing root so that ResolveStoragePath
        // can reconstruct correct paths relative to the FTP working directory.
        var normalizedRoot = listRoot.TrimEnd('/');
        if (!string.IsNullOrEmpty(normalizedRoot) && normalizedRoot != ".")
        {
            var rootPrefix = normalizedRoot + "/";
            for (var i = 0; i < results.Count; i++)
            {
                var (p, s, m) = results[i];
                if (p.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    results[i] = (p[rootPrefix.Length..], s, m);
                }
            }
        }

        logger.LogInformation("FTP ListFiles found {Count} files", results.Count);
        return results;
    }

    private static async Task ListFtpRecursiveAsync(
        AsyncFtpClient client, string path, List<(string path, long size, DateTimeOffset? modified)> results,
        ILogger logger, CancellationToken ct)
    {
        FtpListItem[] items;
        try
        {
            items = await client.GetListing(path, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("FTP GetListing({Path}) failed: {Error}", path, ex.Message);
            return;
        }

        logger.LogInformation("FTP GetListing({Path}) returned {Count} items", path, items.Length);
        foreach (var item in items)
        {
            if (item.Type == FtpObjectType.File)
            {
                results.Add((item.FullName, item.Size,
                    item.Modified != DateTime.MinValue ? new DateTimeOffset(item.Modified, TimeSpan.Zero) : null));
            }
            else if (item.Type == FtpObjectType.Directory)
            {
                await ListFtpRecursiveAsync(client, item.FullName, results, logger, ct);
            }
        }
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
