using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Api.Data;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services.Backend;
using FxSsh;
using FxSsh.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Shared.Interfaces;
using Shared.Models;
using Channel = System.Threading.Channels.Channel;
using static Api.Sftp.SftpConstants;

namespace Api.Sftp;

/// <summary>
/// Implements the SFTP binary protocol (v3) over an SSH channel.
/// Virtual filesystem: / → DataSource dirs → encrypted files (served decrypted).
/// </summary>
public sealed class SftpSubsystem(
    SessionChannel channel,
    IServiceScope scope,
    Guid? userId,
    ILogger logger,
    Action? onDisposed = null)
    : IDisposable
{
    // READDIR pagination
    private const int MaxEntriesPerReaddir = 100;

    // Handle types
    private sealed record DirHandle(List<VfsEntry> Entries, int Offset);
    private sealed record ReadHandle(EncryptedFile File, Stream Stream, long Position);
    private sealed class WriteHandle(StreamingWriteHandle context)
    {
        public StreamingWriteHandle Context { get; } = context;
        public long Position;
    }
    private sealed record VfsEntry(string Name, bool IsDir, long Size, DateTimeOffset Modified);

    // State
    private readonly AppDbContext _db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    private readonly IFileStorageService _fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
    private readonly IEncryptionProviderFactory _encryptionFactory = scope.ServiceProvider.GetRequiredService<IEncryptionProviderFactory>();
    private readonly IBackendStorageProviderFactory _backendStorageFactory = scope.ServiceProvider.GetRequiredService<IBackendStorageProviderFactory>();
    private readonly Dictionary<string, object> _handles = new();
    private readonly System.Threading.Channels.Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<Guid, byte[]> _masterKeyCache = new();
    private readonly HashSet<(Guid dsId, string virtualPath)> _sessionDirs = [];
    private List<DataSource>? _cachedDataSources;
    private int _handleCounter;
    private int _bufferLen;
    private byte[] _buffer = new byte[4096];
    private Task? _processTask;
    private int _disposed;

    public void Start()
    {
        channel.DataReceived += OnData;
        channel.CloseReceived += OnClose;
        _processTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
    }

    private void OnData(object? sender, byte[] data) => _inbound.Writer.TryWrite(data);

    private void OnClose(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _inbound.Writer.TryComplete();
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _inbound.Reader.ReadAllAsync(ct))
            {
                AppendBuffer(chunk);
                while (TryReadPacket(out var type, out var payload))
                {
                    try
                    {
                        await HandlePacketAsync(type, payload);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing SFTP packet type {Type}", type);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "SFTP process loop error");
        }
        finally
        {
            Dispose();
        }
    }

    #region Packet framing

    private void AppendBuffer(byte[] data)
    {
        var needed = _bufferLen + data.Length;
        if (needed > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, needed);
            var newBuf = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, newBuf, 0, _bufferLen);
            _buffer = newBuf;
        }
        Buffer.BlockCopy(data, 0, _buffer, _bufferLen, data.Length);
        _bufferLen += data.Length;
    }

    private bool TryReadPacket(out byte type, out byte[] payload)
    {
        type = 0;
        payload = [];
        if (_bufferLen < 5)
        {
            return false;
        }

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_buffer);
        if (_bufferLen < 4 + length)
        {
            return false;
        }

        type = _buffer[4];
        payload = _buffer[5..(4 + length)];
        // Shift remaining data
        var consumed = 4 + length;
        _bufferLen -= consumed;
        if (_bufferLen > 0)
        {
            Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _bufferLen);
        }

        return true;
    }

    private void Send(byte[] packet) => channel.SendData(packet);

    #endregion

    #region Packet dispatch

    private async Task HandlePacketAsync(byte type, byte[] payload)
    {
        if (type == SSH_FXP_INIT)
        {
            HandleInit(payload);
            return;
        }

        var r = new SftpReader(payload);
        var requestId = r.ReadUInt32();

        switch (type)
        {
            case SSH_FXP_REALPATH: await HandleRealpath(requestId, r); break;
            case SSH_FXP_STAT:
            case SSH_FXP_LSTAT: await HandleStat(requestId, r); break;
            case SSH_FXP_FSTAT: HandleFstat(requestId, r); break;
            case SSH_FXP_OPENDIR: await HandleOpendir(requestId, r); break;
            case SSH_FXP_READDIR: HandleReaddir(requestId, r); break;
            case SSH_FXP_CLOSE: await HandleClose(requestId, r); break;
            case SSH_FXP_OPEN: await HandleOpen(requestId, r); break;
            case SSH_FXP_READ: await HandleRead(requestId, r); break;
            case SSH_FXP_WRITE: await HandleWrite(requestId, r); break;
            case SSH_FXP_REMOVE: await HandleRemove(requestId, r); break;
            case SSH_FXP_RENAME: await HandleRename(requestId, r); break;
            case SSH_FXP_MKDIR: await HandleMkdir(requestId, r); break;
            case SSH_FXP_RMDIR: await HandleRmdir(requestId, r); break;
            case SSH_FXP_SETSTAT:
            case SSH_FXP_FSETSTAT: SendStatus(requestId, SSH_FX_OK); break;
            default:
                logger.LogDebug("Unsupported SFTP packet type {Type}", type);
                SendStatus(requestId, SSH_FX_OP_UNSUPPORTED);
                break;
        }
    }

    private void HandleInit(byte[] payload)
    {
        // Client sends version (uint32). We respond with version 3.
        Send(BuildPacket(SSH_FXP_VERSION, w => w.WriteUInt32(3)));
    }

    #endregion

    #region Path operations

    private async Task HandleRealpath(uint id, SftpReader r)
    {
        var path = NormalizePath(r.ReadString());
        Send(BuildNamePacket(id, path, DirAttrs(DateTimeOffset.UtcNow)));
    }

    private async Task HandleStat(uint id, SftpReader r)
    {
        var path = NormalizePath(r.ReadString());

        if (path == "/")
        {
            Send(BuildAttrsPacket(id, DirAttrs(DateTimeOffset.UtcNow)));
            return;
        }

        var parts = path.TrimStart('/').Split('/', 2);
        var dsName = parts[0];
        var ds = await FindDataSourceByNameAsync(dsName);

        if (ds is null)
        {
            SendStatus(id, SSH_FX_NO_SUCH_FILE);
            return;
        }

        if (parts.Length == 1)
        {
            Send(BuildAttrsPacket(id, DirAttrs(ds.CreatedAt)));
            return;
        }

        var subPath = parts[1];
        var (entry, _) = await FindEntryAsync(ds, subPath);
        if (entry is null)
        {
            SendStatus(id, SSH_FX_NO_SUCH_FILE);
            return;
        }

        Send(BuildAttrsPacket(id, entry.IsDir
            ? DirAttrs(entry.Modified)
            : FileAttrs(entry.Size, entry.Modified)));
    }

    private void HandleFstat(uint id, SftpReader r)
    {
        var handle = r.ReadString();
        if (_handles.TryGetValue(handle, out var h))
        {
            switch (h)
            {
                case DirHandle:
                    Send(BuildAttrsPacket(id, DirAttrs(DateTimeOffset.UtcNow)));
                    return;
                case ReadHandle rh:
                    Send(BuildAttrsPacket(id, FileAttrs(rh.File.OriginalFileSize, rh.File.CreatedAt)));
                    return;
                case WriteHandle:
                    Send(BuildAttrsPacket(id, FileAttrs(0, DateTimeOffset.UtcNow)));
                    return;
            }
        }
        SendStatus(id, SSH_FX_FAILURE);
    }

    #endregion

    #region Directory operations

    private async Task HandleOpendir(uint id, SftpReader r)
    {
        var path = NormalizePath(r.ReadString());
        List<VfsEntry> entries;

        if (path == "/")
        {
            var sources = await GetDataSourcesAsync();
            entries = sources.Select(d => new VfsEntry(d.Name, true, 0, d.CreatedAt)).ToList();
        }
        else
        {
            var parts = path.TrimStart('/').Split('/', 2);
            var ds = await FindDataSourceByNameAsync(parts[0]);
            if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

            var subPath = parts.Length > 1 ? parts[1] + "/" : "";
            entries = await ListDirectoryAsync(ds, subPath);
        }

        var handle = NextHandle();
        _handles[handle] = new DirHandle(entries, 0);
        Send(BuildHandlePacket(id, handle));
    }

    private void HandleReaddir(uint id, SftpReader r)
    {
        var handle = r.ReadString();
        if (!_handles.TryGetValue(handle, out var h) || h is not DirHandle dh)
        {
            SendStatus(id, SSH_FX_FAILURE);
            return;
        }

        if (dh.Offset >= dh.Entries.Count)
        {
            SendStatus(id, SSH_FX_EOF);
            return;
        }

        var batch = dh.Entries.Skip(dh.Offset).Take(MaxEntriesPerReaddir).ToList();
        _handles[handle] = dh with { Offset = dh.Offset + batch.Count };

        Send(BuildPacket(SSH_FXP_NAME, w =>
        {
            w.WriteUInt32(id);
            w.WriteUInt32((uint)batch.Count);
            foreach (var e in batch)
            {
                w.WriteString(e.Name);
                w.WriteString(FormatLongName(e));
                if (e.IsDir)
                {
                    w.WriteAttrs(DirAttrs(e.Modified));
                }
                else
                {
                    w.WriteAttrs(FileAttrs(e.Size, e.Modified));
                }
            }
        }));
    }

    private async Task HandleMkdir(uint id, SftpReader r)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var path = NormalizePath(r.ReadString());
        // Skip attrs
        r.SkipAttrs();

        var parts = path.TrimStart('/').Split('/', 2);
        if (parts.Length < 2)
        {
            // Cannot create data sources via SFTP
            SendStatus(id, SSH_FX_PERMISSION_DENIED);
            return;
        }

        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        var subPath = parts[1].EndsWith('/') ? parts[1] : parts[1] + "/";
        _sessionDirs.Add((ds.Id, subPath));
        SendStatus(id, SSH_FX_OK);
    }

    private async Task HandleRmdir(uint id, SftpReader r)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var path = NormalizePath(r.ReadString());
        var parts = path.TrimStart('/').Split('/', 2);
        if (parts.Length < 2) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        var dirPrefix = parts[1].EndsWith('/') ? parts[1] : parts[1] + "/";
        var masterKey = GetCachedMasterKey(ds);
        var defaultMethod = ds.Backend.EncryptionMethod;

        var allFiles = await _db.EncryptedFiles
            .Where(f => f.DataSourceId == ds.Id)
            .ToListAsync();

        foreach (var f in allFiles)
        {
            try
            {
                var enc = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = enc.DecryptString(f.OriginalFileName, masterKey, iv);
                if (fullPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    await _fileStorage.DeleteFileAsync(f);
                }
            }
            catch
            {
                continue;
            }
        }

        // Remove from session-tracked dirs
        _sessionDirs.RemoveWhere(e => e.dsId == ds.Id &&
            e.virtualPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase));

        InvalidateCache();
        SendStatus(id, SSH_FX_OK);
    }

    #endregion

    #region File operations

    private async Task HandleOpen(uint id, SftpReader r)
    {
        var path = NormalizePath(r.ReadString());
        var pflags = r.ReadUInt32();
        // Skip attrs
        r.SkipAttrs();

        if (path == "/" || !path.Contains('/'))
        {
            SendStatus(id, SSH_FX_PERMISSION_DENIED);
            return;
        }

        var parts = path.TrimStart('/').Split('/', 2);
        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }
        var subPath = parts[1];

        if ((pflags & SSH_FXF_WRITE) != 0)
        {
            if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

            // Delete existing file if TRUNC
            if ((pflags & SSH_FXF_TRUNC) != 0 || (pflags & SSH_FXF_CREAT) != 0)
            {
                var existing = await FindFileAsync(ds, subPath);
                if (existing is not null)
                {
                    await _fileStorage.DeleteFileAsync(existing);
                }
            }

            var mime = new FileExtensionContentTypeProvider().TryGetContentType(subPath, out var ct) ? ct : null;
            var ctx = await _fileStorage.OpenWriteStreamAsync(userId!.Value, ds.Id, subPath, mime);
            var handle = NextHandle();
            _handles[handle] = new WriteHandle(ctx);
            Send(BuildHandlePacket(id, handle));
        }
        else
        {
            var file = await FindFileAsync(ds, subPath);
            if (file is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

            var stream = await _fileStorage.OpenDecryptedStreamAsync(file);

            var handle = NextHandle();
            _handles[handle] = new ReadHandle(file, stream, 0);
            Send(BuildHandlePacket(id, handle));
        }
    }

    private async Task HandleRead(uint id, SftpReader r)
    {
        var handle = r.ReadString();
        var offset = r.ReadUInt64();
        var len = r.ReadUInt32();

        if (!_handles.TryGetValue(handle, out var h) || h is not ReadHandle rh)
        {
            SendStatus(id, SSH_FX_FAILURE);
            return;
        }

        // Skip forward if needed (streams are sequential)
        if ((long)offset > rh.Position)
        {
            var toSkip = (long)offset - rh.Position;
            var skipBuf = new byte[Math.Min(8192, toSkip)];
            while (toSkip > 0)
            {
                var read = await rh.Stream.ReadAsync(skipBuf.AsMemory(0, (int)Math.Min(skipBuf.Length, toSkip)));
                if (read == 0)
                {
                    break;
                }

                toSkip -= read;
            }
        }
        else if ((long)offset < rh.Position)
        {
            // Encrypted streams are sequential — backward seeks are not supported
            SendStatus(id, SSH_FX_FAILURE, "Backward seek not supported");
            return;
        }

        var readLen = (int)Math.Min(len, 32768);
        var buffer = ArrayPool<byte>.Shared.Rent(readLen);
        try
        {
            var bytesRead = await rh.Stream.ReadAsync(buffer.AsMemory(0, readLen));

            if (bytesRead == 0)
            {
                SendStatus(id, SSH_FX_EOF);
                return;
            }

            _handles[handle] = rh with { Position = rh.Position + bytesRead };

            Send(BuildPacket(SSH_FXP_DATA, w =>
            {
                w.WriteUInt32(id);
                w.WriteBytes(buffer.AsSpan(0, bytesRead));
            }));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleWrite(uint id, SftpReader r)
    {
        var handle = r.ReadString();
        var offset = r.ReadUInt64();
        var data = r.ReadBytes();

        if (!_handles.TryGetValue(handle, out var h) || h is not WriteHandle wh)
        {
            SendStatus(id, SSH_FX_FAILURE);
            return;
        }

        if ((long)offset != wh.Position)
        {
            logger.LogWarning("SFTP non-sequential write at offset {Offset}, expected {Expected}", offset, wh.Position);
            SendStatus(id, SSH_FX_FAILURE);
            return;
        }

        await wh.Context.Stream.WriteAsync(data);
        wh.Position += data.Length;
        SendStatus(id, SSH_FX_OK);
    }

    private async Task HandleClose(uint id, SftpReader r)
    {
        var handle = r.ReadString();
        if (!_handles.Remove(handle, out var h))
        {
            SendStatus(id, SSH_FX_FAILURE);
            return;
        }

        switch (h)
        {
            case ReadHandle rh:
                rh.Stream.Dispose();
                break;
            case WriteHandle wh:
                try
                {
                    await wh.Context.CompleteAsync(wh.Position);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to store file via SFTP");
                    await wh.Context.DisposeAsync();
                    SendStatus(id, SSH_FX_FAILURE);
                    return;
                }
                break;
        }

        SendStatus(id, SSH_FX_OK);
    }

    private async Task HandleRemove(uint id, SftpReader r)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var path = NormalizePath(r.ReadString());
        var parts = path.TrimStart('/').Split('/', 2);
        if (parts.Length < 2) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        var file = await FindFileAsync(ds, parts[1]);
        if (file is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        await _fileStorage.DeleteFileAsync(file);
        InvalidateCache();
        SendStatus(id, SSH_FX_OK);
    }

    private async Task HandleRename(uint id, SftpReader r)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var oldPath = NormalizePath(r.ReadString());
        var newPath = NormalizePath(r.ReadString());

        var oldParts = oldPath.TrimStart('/').Split('/', 2);
        var newParts = newPath.TrimStart('/').Split('/', 2);

        if (oldParts.Length < 2 || newParts.Length < 2)
        {
            SendStatus(id, SSH_FX_PERMISSION_DENIED);
            return;
        }

        // Must be within the same data source
        if (!string.Equals(oldParts[0], newParts[0], StringComparison.OrdinalIgnoreCase))
        {
            SendStatus(id, SSH_FX_OP_UNSUPPORTED, "Cannot rename across data sources");
            return;
        }

        var ds = await FindDataSourceByNameAsync(oldParts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        var file = await FindFileAsync(ds, oldParts[1]);
        if (file is not null)
        {
            // Single file rename/move
            var masterKey = GetCachedMasterKey(ds);
            var encryption = _encryptionFactory.GetProvider(file.EncryptionMethod ?? ds.Backend.EncryptionMethod);
            var iv = Convert.FromBase64String(file.IvBase64);

            file.OriginalFileName = encryption.EncryptString(newParts[1], masterKey, iv);

            // For None encryption, also rename the actual file on the backend
            if ((file.EncryptionMethod ?? ds.Backend.EncryptionMethod) == EncryptionMethod.None)
            {
                var connection = ds.ToBackendConnectionInfo();
                file.StoragePath = await _backendStorageFactory.GetProvider(ds.Backend.Protocol).RenameAsync(connection, file.StoragePath, newParts[1]);
            }

            await _db.SaveChangesAsync();
            InvalidateCache();
            SendStatus(id, SSH_FX_OK);
            return;
        }

        // Directory rename/move — re-prefix all files under the old path
        var oldPrefix = oldParts[1].EndsWith('/') ? oldParts[1] : oldParts[1] + "/";
        var newPrefix = newParts[1].EndsWith('/') ? newParts[1] : newParts[1] + "/";

        var masterKey2 = GetCachedMasterKey(ds);
        var defaultMethod = ds.Backend.EncryptionMethod;

        var allFiles = await _db.EncryptedFiles
            .Where(f => f.DataSourceId == ds.Id)
            .ToListAsync();

        var moved = 0;
        foreach (var f in allFiles)
        {
            string fullPath;
            try
            {
                var enc = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
                var iv = Convert.FromBase64String(f.IvBase64);
                fullPath = enc.DecryptString(f.OriginalFileName, masterKey2, iv);
            }
            catch
            {
                continue;
            }

            if (!fullPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var newFullPath = newPrefix + fullPath[oldPrefix.Length..];
            var enc2 = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
            var iv2 = Convert.FromBase64String(f.IvBase64);
            f.OriginalFileName = enc2.EncryptString(newFullPath, masterKey2, iv2);

            if ((f.EncryptionMethod ?? defaultMethod) == EncryptionMethod.None)
            {
                var connection = ds.ToBackendConnectionInfo();
                f.StoragePath = await _backendStorageFactory.GetProvider(ds.Backend.Protocol).RenameAsync(connection, f.StoragePath, newFullPath);
            }

            moved++;
        }

        // Update session-tracked dirs
        var oldSessionDirs = _sessionDirs
            .Where(e => e.dsId == ds.Id && e.virtualPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var old in oldSessionDirs)
        {
            _sessionDirs.Remove(old);
            _sessionDirs.Add((ds.Id, newPrefix + old.virtualPath[oldPrefix.Length..]));
        }

        // If no files were moved but the dir exists as a session-tracked empty dir, still succeed
        if (moved == 0 && oldSessionDirs.Count == 0)
        {
            SendStatus(id, SSH_FX_NO_SUCH_FILE);
            return;
        }

        if (moved > 0)
        {
            await _db.SaveChangesAsync();
        }

        InvalidateCache();
        SendStatus(id, SSH_FX_OK);
    }

    #endregion

    #region Virtual filesystem helpers

    private bool IsAuthenticated => userId.HasValue;

    private void InvalidateCache() => _cachedDataSources = null;

    private async Task<List<DataSource>> GetDataSourcesAsync()
    {
        if (_cachedDataSources is not null)
        {
            return _cachedDataSources;
        }

        if (userId.HasValue)
        {
            _cachedDataSources = await _db.DataSources
                .Include(d => d.Frontends)
                .Where(d => d.UserId == userId && d.Frontends.Any(f => f.Type == FrontendType.Sftp))
                .OrderBy(d => d.Name).ToListAsync();
        }
        else
        {
            _cachedDataSources = await _db.DataSources
                .Include(d => d.Frontends)
                .Where(d => d.Frontends.Any(f => f.Type == FrontendType.Sftp && f.AllowAnonymous))
                .OrderBy(d => d.Name).ToListAsync();
        }

        return _cachedDataSources;
    }

    private async Task<DataSource?> FindDataSourceByNameAsync(string name)
    {
        var sources = await GetDataSourcesAsync();
        return sources.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private byte[] GetCachedMasterKey(DataSource ds)
    {
        if (_masterKeyCache.TryGetValue(ds.Id, out var cached))
        {
            return cached;
        }

        var key = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        _masterKeyCache[ds.Id] = key;
        return key;
    }

    private async Task<List<VfsEntry>> ListDirectoryAsync(DataSource ds, string pathPrefix)
    {
        var masterKey = GetCachedMasterKey(ds);
        var defaultMethod = ds.Backend.EncryptionMethod;

        var files = await _db.EncryptedFiles
            .Where(f => f.DataSourceId == ds.Id)
            .ToListAsync();

        var entries = new List<VfsEntry>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            try
            {
                var encryption = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);

                if (!string.IsNullOrEmpty(pathPrefix) &&
                    !fullPath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = string.IsNullOrEmpty(pathPrefix) ? fullPath : fullPath[pathPrefix.Length..];
                var slashIdx = relativePath.IndexOf('/');

                if (slashIdx < 0)
                {
                    entries.Add(new VfsEntry(relativePath, false, f.OriginalFileSize, f.CreatedAt));
                }
                else
                {
                    var folderName = relativePath[..slashIdx];
                    if (seenFolders.Add(folderName))
                    {
                        entries.Add(new VfsEntry(folderName, true, 0, f.CreatedAt));
                    }
                }
            }
            catch
            {
                // Skip files that can't be decrypted
            }
        }

        // Include session-tracked empty directories (matching FTP behavior)
        foreach (var (sid, vpath) in _sessionDirs)
        {
            if (sid != ds.Id || !vpath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) || vpath == pathPrefix)
                continue;
            var rel = vpath[pathPrefix.Length..].TrimEnd('/');
            if (!rel.Contains('/') && seenFolders.Add(rel))
            {
                entries.Add(new VfsEntry(rel, true, 0, DateTimeOffset.UtcNow));
            }
        }

        return entries.OrderBy(e => !e.IsDir).ThenBy(e => e.Name).ToList();
    }

    private async Task<(VfsEntry? entry, EncryptedFile? file)> FindEntryAsync(DataSource ds, string subPath)
    {
        var masterKey = GetCachedMasterKey(ds);
        var defaultMethod = ds.Backend.EncryptionMethod;

        var files = await _db.EncryptedFiles
            .Where(f => f.DataSourceId == ds.Id)
            .ToListAsync();

        // Check for exact file match
        foreach (var f in files)
        {
            try
            {
                var encryption = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);

                if (string.Equals(fullPath, subPath, StringComparison.OrdinalIgnoreCase))
                {
                    return (new VfsEntry(Path.GetFileName(subPath), false, f.OriginalFileSize, f.CreatedAt), f);
                }
            }
            catch
            {
                continue;
            }
        }

        // Check for directory prefix
        var dirPrefix = subPath.EndsWith('/') ? subPath : subPath + "/";
        var isDir = files.Any(f =>
        {
            try
            {
                var enc = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = enc.DecryptString(f.OriginalFileName, masterKey, iv);
                return fullPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        if (isDir)
        {
            return (new VfsEntry(Path.GetFileName(subPath.TrimEnd('/')), true, 0, DateTimeOffset.UtcNow), null);
        }

        // Check session-tracked empty directories
        if (_sessionDirs.Contains((ds.Id, dirPrefix)))
        {
            return (new VfsEntry(Path.GetFileName(subPath.TrimEnd('/')), true, 0, DateTimeOffset.UtcNow), null);
        }

        return (null, null);
    }

    private async Task<EncryptedFile?> FindFileAsync(DataSource ds, string subPath)
    {
        var masterKey = GetCachedMasterKey(ds);
        var defaultMethod = ds.Backend.EncryptionMethod;

        var files = await _db.EncryptedFiles
            .Where(f => f.DataSourceId == ds.Id)
            .ToListAsync();

        foreach (var f in files)
        {
            try
            {
                var encryption = _encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);
                if (string.Equals(fullPath, subPath, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }

    #endregion

    #region Binary protocol helpers

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return "/";
        }

        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // Resolve ".." segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == "..") { if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }
            else if (seg != ".")
            {
                stack.Push(seg);
            }
        }
        return "/" + string.Join("/", stack.Reverse());
    }

    private string NextHandle() => $"h{Interlocked.Increment(ref _handleCounter)}";

    private static string FormatLongName(VfsEntry e)
    {
        var perms = e.IsDir ? "drwxr-xr-x" : "-rw-r--r--";
        var size = e.Size.ToString().PadLeft(13);
        var date = e.Modified.ToString("MMM dd HH:mm");
        return $"{perms}   1 owner    group    {size} {date} {e.Name}";
    }

    private static byte[] DirAttrs(DateTimeOffset modified)
    {
        return WriteAttrsBytes(ATTR_PERMS | ATTR_ACMODTIME, 0, DIR_MODE, modified);
    }

    private static byte[] FileAttrs(long size, DateTimeOffset modified)
    {
        return WriteAttrsBytes(ATTR_SIZE | ATTR_PERMS | ATTR_ACMODTIME, size, FILE_MODE, modified);
    }

    private static byte[] WriteAttrsBytes(uint flags, long size, uint perms, DateTimeOffset modified)
    {
        using var ms = new MemoryStream();
        var w = new SftpWriter(ms);
        w.WriteUInt32(flags);
        if ((flags & ATTR_SIZE) != 0)
        {
            w.WriteUInt64((ulong)size);
        }

        if ((flags & ATTR_PERMS) != 0)
        {
            w.WriteUInt32(perms);
        }

        if ((flags & ATTR_ACMODTIME) != 0)
        {
            var unix = (uint)modified.ToUnixTimeSeconds();
            w.WriteUInt32(unix);
            w.WriteUInt32(unix);
        }
        return ms.ToArray();
    }

    private void SendStatus(uint requestId, uint code, string message = "")
    {
        Send(BuildPacket(SSH_FXP_STATUS, w =>
        {
            w.WriteUInt32(requestId);
            w.WriteUInt32(code);
            w.WriteString(message);
            w.WriteString("en");
        }));
    }

    private static byte[] BuildHandlePacket(uint requestId, string handle)
    {
        return BuildPacket(SSH_FXP_HANDLE, w =>
        {
            w.WriteUInt32(requestId);
            w.WriteString(handle);
        });
    }

    private static byte[] BuildNamePacket(uint requestId, string name, byte[] attrs)
    {
        return BuildPacket(SSH_FXP_NAME, w =>
        {
            w.WriteUInt32(requestId);
            w.WriteUInt32(1); // count
            w.WriteString(name);
            w.WriteString(name); // longname
            w.WriteRaw(attrs);
        });
    }

    private static byte[] BuildAttrsPacket(uint requestId, byte[] attrs)
    {
        return BuildPacket(SSH_FXP_ATTRS, w =>
        {
            w.WriteUInt32(requestId);
            w.WriteRaw(attrs);
        });
    }

    private static byte[] BuildPacket(byte type, Action<SftpWriter> write)
    {
        using var ms = new MemoryStream();
        var w = new SftpWriter(ms);
        w.WriteUInt32(0); // placeholder for length
        ms.WriteByte(type);
        write(w);

        var data = ms.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)(data.Length - 4));
        return data;
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        _inbound.Writer.TryComplete();

        foreach (var h in _handles.Values)
        {
            if (h is ReadHandle rh)
            {
                rh.Stream.Dispose();
            }

            if (h is WriteHandle wh)
            {
                wh.Context.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        _handles.Clear();

        scope.Dispose();
        _cts.Dispose();
        onDisposed?.Invoke();
    }

    #endregion

    #region Binary reader/writer

    private sealed class SftpReader(byte[] data)
    {
        private int _pos = 0;

        public uint ReadUInt32()
        {
            var val = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_pos, 4));
            _pos += 4;
            return val;
        }

        public ulong ReadUInt64()
        {
            var val = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(_pos, 8));
            _pos += 8;
            return val;
        }

        public string ReadString()
        {
            var len = (int)ReadUInt32();
            var s = Encoding.UTF8.GetString(data, _pos, len);
            _pos += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            var len = (int)ReadUInt32();
            var result = new byte[len];
            Buffer.BlockCopy(data, _pos, result, 0, len);
            _pos += len;
            return result;
        }

        public void SkipAttrs()
        {
            if (_pos >= data.Length)
            {
                return;
            }

            var flags = ReadUInt32();
            if ((flags & ATTR_SIZE) != 0)
            {
                _pos += 8;
            }

            if ((flags & ATTR_UIDGID) != 0)
            {
                _pos += 8;
            }

            if ((flags & ATTR_PERMS) != 0)
            {
                _pos += 4;
            }

            if ((flags & ATTR_ACMODTIME) != 0)
            {
                _pos += 8;
            }
        }
    }

    private sealed class SftpWriter(Stream stream)
    {
        public void WriteUInt32(uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        public void WriteUInt64(ulong value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buf, value);
            stream.Write(buf);
        }

        public void WriteString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteUInt32((uint)bytes.Length);
            stream.Write(bytes);
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            WriteUInt32((uint)data.Length);
            stream.Write(data);
        }

        public void WriteRaw(byte[] data) => stream.Write(data);

        public void WriteAttrs(byte[] attrs) => stream.Write(attrs);
    }

    #endregion
}
