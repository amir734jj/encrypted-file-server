using Api.Interfaces;
using FluentFTP;
using Microsoft.AspNetCore.StaticFiles;
using Renci.SshNet;
using Shared.Contracts;
using Shared.Interfaces;

namespace Api.Services;

public interface IRemoteImportService
{
    Task<RemoteBrowseResponse> BrowseAsync(RemoteBrowseRequest request, CancellationToken ct = default);

    Task<RemoteImportResult> ImportAsync(
        Guid userId, Guid dataSourceId, RemoteImportRequest request,
        Action<BulkOperationProgress>? onProgress = null, CancellationToken ct = default);
}

public sealed class RemoteImportService(IFileStorageService fileStorage) : IRemoteImportService
{
    public async Task<RemoteBrowseResponse> BrowseAsync(RemoteBrowseRequest request, CancellationToken ct = default)
    {
        var conn = request.Connection;
        var path = NormalizePath(request.Path);

        return conn.Protocol switch
        {
            BackendStorageType.FtpClient => await BrowseFtpAsync(conn, path, ct),
            BackendStorageType.SftpClient => BrowseSftp(conn, path),
            _ => throw new NotSupportedException($"Unsupported protocol: {conn.Protocol}")
        };
    }

    public async Task<RemoteImportResult> ImportAsync(
        Guid userId, Guid dataSourceId, RemoteImportRequest request,
        Action<BulkOperationProgress>? onProgress = null, CancellationToken ct = default)
    {
        var conn = request.Connection;
        var remotePath = NormalizePath(request.RemotePath);
        var targetPrefix = (request.TargetPath ?? "").Trim('/');
        if (!string.IsNullOrEmpty(targetPrefix)) targetPrefix += "/";

        // First, collect all files recursively
        var files = new List<(string remotePath, long size)>();
        await CollectFilesRecursiveAsync(conn, remotePath, files, ct);

        var imported = 0;
        var failed = 0;
        var errors = new List<string>();

        onProgress?.Invoke(new BulkOperationProgress("Import", files.Count, 0));

        foreach (var (filePath, _) in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Compute relative path from the import root
                var relativePath = filePath;
                if (remotePath != "/" && filePath.StartsWith(remotePath, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = filePath[remotePath.Length..].TrimStart('/');
                }

                var storageName = targetPrefix + relativePath;
                var contentType = InferContentType(Path.GetFileName(filePath));

                await using var stream = await OpenRemoteReadAsync(conn, filePath, ct);
                await fileStorage.StoreFileAsync(userId, dataSourceId, storageName, contentType, stream);
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 50) // cap error list
                    errors.Add($"{filePath}: {ex.Message}");
            }

            onProgress?.Invoke(new BulkOperationProgress("Import", files.Count, imported + failed));
        }

        return new RemoteImportResult(files.Count, imported, failed, errors);
    }

    #region FTP

    private static async Task<RemoteBrowseResponse> BrowseFtpAsync(
        RemoteConnectionRequest conn, string path, CancellationToken ct)
    {
        using var client = await ConnectFtpAsync(conn, ct);
        var items = await client.GetListing(path, ct);
        var entries = items
            .Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory)
            .Select(i => new RemoteEntryDto(
                i.Name,
                i.FullName,
                i.Type == FtpObjectType.Directory,
                i.Size,
                i.Modified != DateTime.MinValue ? new DateTimeOffset(i.Modified, TimeSpan.Zero) : null))
            .OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name)
            .ToList();

        return new RemoteBrowseResponse(path, entries);
    }

    private static async Task CollectFtpFilesAsync(
        RemoteConnectionRequest conn, string path, List<(string remotePath, long size)> files, CancellationToken ct)
    {
        using var client = await ConnectFtpAsync(conn, ct);
        var items = await client.GetListing(path, FtpListOption.Recursive, ct);
        foreach (var item in items)
        {
            if (item.Type == FtpObjectType.File)
            {
                files.Add((item.FullName, item.Size));
            }
        }
    }

    private static async Task<Stream> OpenFtpReadAsync(RemoteConnectionRequest conn, string path, CancellationToken ct)
    {
        var client = await ConnectFtpAsync(conn, ct);
        var stream = await client.OpenRead(path, token: ct);
        return new OwningStream(stream, client);
    }

    private static async Task<AsyncFtpClient> ConnectFtpAsync(RemoteConnectionRequest conn, CancellationToken ct)
    {
        var client = new AsyncFtpClient(conn.Host, conn.Username, conn.Password, conn.Port);
        if (conn.UseSsl)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true;
        }
        await client.Connect(ct);
        return client;
    }

    #endregion

    #region SFTP

    private static RemoteBrowseResponse BrowseSftp(RemoteConnectionRequest conn, string path)
    {
        using var client = ConnectSftp(conn);
        var items = client.ListDirectory(path);
        var entries = items
            .Where(i => i.Name != "." && i.Name != "..")
            .Select(i => new RemoteEntryDto(
                i.Name,
                i.FullName,
                i.IsDirectory,
                i.Length,
                i.LastWriteTime != DateTime.MinValue ? new DateTimeOffset(i.LastWriteTime, TimeSpan.Zero) : null))
            .OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name)
            .ToList();

        return new RemoteBrowseResponse(path, entries);
    }

    private static void CollectSftpFilesRecursive(
        SftpClient client, string path, List<(string remotePath, long size)> files)
    {
        foreach (var item in client.ListDirectory(path))
        {
            if (item.Name == "." || item.Name == "..") continue;
            if (item.IsDirectory)
            {
                CollectSftpFilesRecursive(client, item.FullName, files);
            }
            else
            {
                files.Add((item.FullName, item.Length));
            }
        }
    }

    private static Stream OpenSftpRead(RemoteConnectionRequest conn, string path)
    {
        var client = ConnectSftp(conn);
        var stream = client.OpenRead(path);
        return new OwningStream(stream, client);
    }

    private static SftpClient ConnectSftp(RemoteConnectionRequest conn)
    {
        var client = new SftpClient(conn.Host, conn.Port, conn.Username, conn.Password);
        client.Connect();
        return client;
    }

    #endregion

    #region Helpers

    private async Task CollectFilesRecursiveAsync(
        RemoteConnectionRequest conn, string path,
        List<(string remotePath, long size)> files, CancellationToken ct)
    {
        switch (conn.Protocol)
        {
            case BackendStorageType.FtpClient:
                await CollectFtpFilesAsync(conn, path, files, ct);
                break;
            case BackendStorageType.SftpClient:
                using (var client = ConnectSftp(conn))
                {
                    CollectSftpFilesRecursive(client, path, files);
                }
                break;
        }
    }

    private async Task<Stream> OpenRemoteReadAsync(
        RemoteConnectionRequest conn, string path, CancellationToken ct)
    {
        return conn.Protocol switch
        {
            BackendStorageType.FtpClient => await OpenFtpReadAsync(conn, path, ct),
            BackendStorageType.SftpClient => OpenSftpRead(conn, path),
            _ => throw new NotSupportedException()
        };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }

    private static string? InferContentType(string fileName)
    {
        return new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var ct) ? ct : null;
    }

    /// <summary>
    /// Stream wrapper that disposes a parent IDisposable (FTP/SFTP client) when the stream is disposed.
    /// </summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
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
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            owner.Dispose();
            await base.DisposeAsync();
        }
    }

    #endregion
}
