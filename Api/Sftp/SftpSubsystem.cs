using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Api.Data.Entities;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using FxSsh;
using FxSsh.Services;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Models;
using Channel = System.Threading.Channels.Channel;
using static Api.Sftp.SftpConstants;

namespace Api.Sftp;

/// <summary>
/// Implements the SFTP binary protocol (v3) over an SSH channel.
/// Virtual filesystem: / -> DataSource dirs -> files on backend storage.
/// </summary>
public sealed class SftpSubsystem(
    SessionChannel channel,
    IServiceScope scope,
    Guid? userId,
    ILogger logger,
    Action? onDisposed = null)
    : IDisposable
{
    private const int MaxEntriesPerReaddir = 100;

    private sealed record DirHandle(List<VfsEntry> Entries, int Offset);
    private sealed record ReadHandle(DataSource Ds, string RelativePath, Stream Stream, long Position);
    private sealed class WriteHandle(StreamingWriteHandle context)
    {
        public StreamingWriteHandle Context { get; } = context;
        public long Position;
    }
    private sealed record VfsEntry(string Name, bool IsDir, long Size, DateTimeOffset Modified);

    private readonly IEfRepository _repository = scope.ServiceProvider.GetRequiredService<IEfRepository>();
    private readonly IFileStorageService _fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
    private readonly Dictionary<string, object> _handles = new();
    private readonly System.Threading.Channels.Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<(Guid dsId, string virtualPath)> _sessionDirs = [];
    private List<DataSource>? _cachedDataSources;
    private int _handleCounter;
    private int _bufferLen;
    private byte[] _buffer = new byte[4096];
    private Task? _processTask;
    private int _disposed;

    private IBasicCrud<DataSource> DataSourceDal => _repository.For<DataSource>();

    public void Start()
    {
        channel.DataReceived += OnData;
        channel.CloseReceived += OnClose;
        _processTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
    }

    private void OnData(object? sender, byte[] data) => _inbound.Writer.TryWrite(data);
    private void OnClose(object? sender, EventArgs e) { _cts.Cancel(); _inbound.Writer.TryComplete(); }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _inbound.Reader.ReadAllAsync(ct))
            {
                AppendBuffer(chunk);
                while (TryReadPacket(out var type, out var payload))
                {
                    try { await HandlePacketAsync(type, payload); }
                    catch (Exception ex) { logger.LogError(ex, "Error processing SFTP packet type {Type}", type); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "SFTP process loop error"); }
        finally { Dispose(); }
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
        type = 0; payload = [];
        if (_bufferLen < 5) return false;
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_buffer);
        if (_bufferLen < 4 + length) return false;
        type = _buffer[4];
        payload = _buffer[5..(4 + length)];
        var consumed = 4 + length;
        _bufferLen -= consumed;
        if (_bufferLen > 0) Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _bufferLen);
        return true;
    }

    private void Send(byte[] packet) => channel.SendData(packet);

    #endregion

    #region Packet dispatch

    private async Task HandlePacketAsync(byte type, byte[] payload)
    {
        if (type == SSH_FXP_INIT) { HandleInit(payload); return; }
        var r = new SftpReader(payload);
        var requestId = r.ReadUInt32();
        switch (type)
        {
            case SSH_FXP_REALPATH:
            {
                var path = NormalizePath(r.ReadString());
                await HandleRealpath(requestId, path);
                break;
            }
            case SSH_FXP_STAT:
            case SSH_FXP_LSTAT:
            {
                var path = NormalizePath(r.ReadString());
                await HandleStat(requestId, path);
                break;
            }
            case SSH_FXP_FSTAT:
            {
                var handle = r.ReadString();
                HandleFstat(requestId, handle);
                break;
            }
            case SSH_FXP_OPENDIR:
            {
                var path = NormalizePath(r.ReadString());
                await HandleOpendir(requestId, path);
                break;
            }
            case SSH_FXP_READDIR:
            {
                var handle = r.ReadString();
                HandleReaddir(requestId, handle);
                break;
            }
            case SSH_FXP_CLOSE:
            {
                var handle = r.ReadString();
                await HandleClose(requestId, handle);
                break;
            }
            case SSH_FXP_OPEN:
            {
                var path = NormalizePath(r.ReadString());
                var pflags = r.ReadUInt32();
                r.SkipAttrs();
                await HandleOpen(requestId, path, pflags);
                break;
            }
            case SSH_FXP_READ:
            {
                var handle = r.ReadString();
                var offset = r.ReadUInt64();
                var len = r.ReadUInt32();
                await HandleRead(requestId, handle, offset, len);
                break;
            }
            case SSH_FXP_WRITE:
            {
                var handle = r.ReadString();
                var offset = r.ReadUInt64();
                var data = r.ReadBytes();
                await HandleWrite(requestId, handle, offset, data);
                break;
            }
            case SSH_FXP_REMOVE:
            {
                var path = NormalizePath(r.ReadString());
                await HandleRemove(requestId, path);
                break;
            }
            case SSH_FXP_RENAME:
            {
                var oldPath = NormalizePath(r.ReadString());
                var newPath = NormalizePath(r.ReadString());
                await HandleRename(requestId, oldPath, newPath);
                break;
            }
            case SSH_FXP_MKDIR:
            {
                var path = NormalizePath(r.ReadString());
                r.SkipAttrs();
                await HandleMkdir(requestId, path);
                break;
            }
            case SSH_FXP_RMDIR:
            {
                var path = NormalizePath(r.ReadString());
                await HandleRmdir(requestId, path);
                break;
            }
            case SSH_FXP_SETSTAT:
            case SSH_FXP_FSETSTAT: SendStatus(requestId, SSH_FX_OK); break;
            default: SendStatus(requestId, SSH_FX_OP_UNSUPPORTED); break;
        }
    }

    private void HandleInit(byte[] payload) => Send(BuildPacket(SSH_FXP_VERSION, w => w.WriteUInt32(3)));

    #endregion

    #region Path operations

    private Task HandleRealpath(uint id, string path)
    {
        Send(BuildNamePacket(id, path, DirAttrs(DateTimeOffset.UtcNow)));
        return Task.CompletedTask;
    }

    private async Task HandleStat(uint id, string path)
    {
        if (path == "/") { Send(BuildAttrsPacket(id, DirAttrs(DateTimeOffset.UtcNow))); return; }

        var parts = path.TrimStart('/').Split('/', 2);
        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        if (parts.Length == 1) { Send(BuildAttrsPacket(id, DirAttrs(ds.CreatedAt))); return; }

        var subPath = parts[1];
        var entry = await FindEntryAsync(ds, subPath);
        if (entry is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        Send(BuildAttrsPacket(id, entry.IsDir
            ? DirAttrs(entry.Modified)
            : FileAttrs(entry.Size, entry.Modified)));
    }

    private void HandleFstat(uint id, string handle)
    {
        if (_handles.TryGetValue(handle, out var h))
        {
            switch (h)
            {
                case DirHandle: Send(BuildAttrsPacket(id, DirAttrs(DateTimeOffset.UtcNow))); return;
                case ReadHandle rh: Send(BuildAttrsPacket(id, FileAttrs(rh.Stream.Length, DateTimeOffset.UtcNow))); return;
                case WriteHandle: Send(BuildAttrsPacket(id, FileAttrs(0, DateTimeOffset.UtcNow))); return;
            }
        }
        SendStatus(id, SSH_FX_FAILURE);
    }

    #endregion

    #region Directory operations

    private async Task HandleOpendir(uint id, string path)
    {
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

    private void HandleReaddir(uint id, string handle)
    {
        if (!_handles.TryGetValue(handle, out var h) || h is not DirHandle dh) { SendStatus(id, SSH_FX_FAILURE); return; }
        if (dh.Offset >= dh.Entries.Count) { SendStatus(id, SSH_FX_EOF); return; }

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
                w.WriteAttrs(e.IsDir ? DirAttrs(e.Modified) : FileAttrs(e.Size, e.Modified));
            }
        }));
    }

    private async Task HandleMkdir(uint id, string path)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var parts = path.TrimStart('/').Split('/', 2);
        if (parts.Length < 2) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        var subPath = parts[1].EndsWith('/') ? parts[1] : parts[1] + "/";
        _sessionDirs.Add((ds.Id, subPath));
        SendStatus(id, SSH_FX_OK);
    }

    private async Task HandleRmdir(uint id, string path)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }
        var parts = path.TrimStart('/').Split('/', 2);
        if (parts.Length < 2) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        var dirPrefix = parts[1].EndsWith('/') ? parts[1] : parts[1] + "/";
        var allFiles = await _fileStorage.ListFilesAsync(ds);

        foreach (var f in allFiles)
        {
            if (f.Path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await _fileStorage.DeleteFileAsync(ds, f.Path);
            }
        }

        _sessionDirs.RemoveWhere(e => e.dsId == ds.Id &&
            e.virtualPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase));
        InvalidateCache();
        SendStatus(id, SSH_FX_OK);
    }

    #endregion

    #region File operations

    private async Task HandleOpen(uint id, string path, uint pflags)
    {
        if (path == "/" || !path.Contains('/')) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var parts = path.TrimStart('/').Split('/', 2);
        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }
        var subPath = parts[1];

        if ((pflags & SSH_FXF_WRITE) != 0)
        {
            if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

            // Delete existing file if TRUNC/CREAT
            if ((pflags & SSH_FXF_TRUNC) != 0 || (pflags & SSH_FXF_CREAT) != 0)
            {
                if (await _fileStorage.ExistsAsync(ds, subPath))
                {
                    await _fileStorage.DeleteFileAsync(ds, subPath);
                }
            }

            var mime = new FileExtensionContentTypeProvider().TryGetContentType(subPath, out var ct) ? ct : null;
            var ctx = await _fileStorage.OpenWriteStreamAsync(ds, subPath, mime);
            var handle = NextHandle();
            _handles[handle] = new WriteHandle(ctx);
            Send(BuildHandlePacket(id, handle));
        }
        else
        {
            if (!await _fileStorage.ExistsAsync(ds, subPath)) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

            var stream = await _fileStorage.OpenDecryptedStreamAsync(ds, subPath);
            var handle = NextHandle();
            _handles[handle] = new ReadHandle(ds, subPath, stream, 0);
            Send(BuildHandlePacket(id, handle));
        }
    }

    private async Task HandleRead(uint id, string handle, ulong offset, uint len)
    {
        if (!_handles.TryGetValue(handle, out var h) || h is not ReadHandle rh) { SendStatus(id, SSH_FX_FAILURE); return; }

        if ((long)offset > rh.Position)
        {
            var toSkip = (long)offset - rh.Position;
            var skipBuf = new byte[Math.Min(8192, toSkip)];
            while (toSkip > 0)
            {
                var read = await rh.Stream.ReadAsync(skipBuf.AsMemory(0, (int)Math.Min(skipBuf.Length, toSkip)));
                if (read == 0) break;
                toSkip -= read;
            }
        }
        else if ((long)offset < rh.Position)
        {
            SendStatus(id, SSH_FX_FAILURE, "Backward seek not supported");
            return;
        }

        var readLen = (int)Math.Min(len, 32768);
        var buffer = ArrayPool<byte>.Shared.Rent(readLen);
        try
        {
            var bytesRead = await rh.Stream.ReadAsync(buffer.AsMemory(0, readLen));
            if (bytesRead == 0) { SendStatus(id, SSH_FX_EOF); return; }
            _handles[handle] = rh with { Position = rh.Position + bytesRead };
            Send(BuildPacket(SSH_FXP_DATA, w => { w.WriteUInt32(id); w.WriteBytes(buffer.AsSpan(0, bytesRead)); }));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task HandleWrite(uint id, string handle, ulong offset, byte[] data)
    {
        if (!_handles.TryGetValue(handle, out var h) || h is not WriteHandle wh) { SendStatus(id, SSH_FX_FAILURE); return; }

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

    private async Task HandleClose(uint id, string handle)
    {
        if (!_handles.Remove(handle, out var h)) { SendStatus(id, SSH_FX_FAILURE); return; }

        switch (h)
        {
            case ReadHandle rh: rh.Stream.Dispose(); break;
            case WriteHandle wh:
                try { await wh.Context.CompleteAsync(); }
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

    private async Task HandleRemove(uint id, string path)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }
        var parts = path.TrimStart('/').Split('/', 2);
        if (parts.Length < 2) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var ds = await FindDataSourceByNameAsync(parts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        if (!await _fileStorage.ExistsAsync(ds, parts[1])) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        await _fileStorage.DeleteFileAsync(ds, parts[1]);
        InvalidateCache();
        SendStatus(id, SSH_FX_OK);
    }

    private async Task HandleRename(uint id, string oldPath, string newPath)
    {
        if (!IsAuthenticated) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }

        var oldParts = oldPath.TrimStart('/').Split('/', 2);
        var newParts = newPath.TrimStart('/').Split('/', 2);
        if (oldParts.Length < 2 || newParts.Length < 2) { SendStatus(id, SSH_FX_PERMISSION_DENIED); return; }
        if (!string.Equals(oldParts[0], newParts[0], StringComparison.OrdinalIgnoreCase))
        {
            SendStatus(id, SSH_FX_OP_UNSUPPORTED, "Cannot rename across data sources"); return;
        }

        var ds = await FindDataSourceByNameAsync(oldParts[0]);
        if (ds is null) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }

        // Try single file rename
        if (await _fileStorage.ExistsAsync(ds, oldParts[1]))
        {
            await _fileStorage.RenameFileAsync(ds, oldParts[1], newParts[1]);
            InvalidateCache();
            SendStatus(id, SSH_FX_OK);
            return;
        }

        // Directory rename -- re-prefix all files under the old path
        var oldPrefix = oldParts[1].EndsWith('/') ? oldParts[1] : oldParts[1] + "/";
        var newPrefix = newParts[1].EndsWith('/') ? newParts[1] : newParts[1] + "/";

        var allFiles = await _fileStorage.ListFilesAsync(ds);
        var moved = 0;

        foreach (var f in allFiles)
        {
            if (!f.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var newFilePath = newPrefix + f.Path[oldPrefix.Length..];
            await _fileStorage.RenameFileAsync(ds, f.Path, newFilePath);
            moved++;
        }

        // Update session-tracked dirs
        var oldSessionDirs = _sessionDirs
            .Where(e => e.dsId == ds.Id && e.virtualPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var old in oldSessionDirs)
        {
            _sessionDirs.Remove(old);
            _sessionDirs.Add((ds.Id, newPrefix + old.virtualPath[oldPrefix.Length..]));
        }

        if (moved == 0 && oldSessionDirs.Count == 0) { SendStatus(id, SSH_FX_NO_SUCH_FILE); return; }
        InvalidateCache();
        SendStatus(id, SSH_FX_OK);
    }

    #endregion

    #region Virtual filesystem helpers

    private bool IsAuthenticated => userId.HasValue;
    private void InvalidateCache() => _cachedDataSources = null;

    private async Task<List<DataSource>> GetDataSourcesAsync()
    {
        if (_cachedDataSources is not null) return _cachedDataSources;

        _cachedDataSources = userId.HasValue
            ? (await DataSourceDal.GetAll(
                filterExprs: [d => d.UserId == userId && d.Frontends.Any(f => f.Type == FrontendType.Sftp)],
                project: d => d)).OrderBy(d => d.Name).ToList()
            : (await DataSourceDal.GetAll(
                filterExprs: [d => d.Frontends.Any(f => f.Type == FrontendType.Sftp && f.AllowAnonymous)],
                project: d => d)).OrderBy(d => d.Name).ToList();

        return _cachedDataSources;
    }

    private async Task<DataSource?> FindDataSourceByNameAsync(string name)
    {
        var sources = await GetDataSourcesAsync();
        return sources.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<VfsEntry>> ListDirectoryAsync(DataSource ds, string pathPrefix)
    {
        var files = await _fileStorage.ListFilesAsync(ds);
        var entries = new List<VfsEntry>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            if (!string.IsNullOrEmpty(pathPrefix) &&
                !f.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = string.IsNullOrEmpty(pathPrefix) ? f.Path : f.Path[pathPrefix.Length..];
            var slashIdx = relativePath.IndexOf('/');

            if (slashIdx < 0)
            {
                entries.Add(new VfsEntry(relativePath, false, f.StoredSize, f.Modified ?? DateTimeOffset.UtcNow));
            }
            else
            {
                var folderName = relativePath[..slashIdx];
                if (seenFolders.Add(folderName))
                {
                    entries.Add(new VfsEntry(folderName, true, 0, f.Modified ?? DateTimeOffset.UtcNow));
                }
            }
        }

        // Include session-tracked empty directories
        foreach (var (sid, vpath) in _sessionDirs)
        {
            if (sid != ds.Id || !vpath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) || vpath == pathPrefix)
                continue;
            var rel = vpath[pathPrefix.Length..].TrimEnd('/');
            if (!rel.Contains('/') && seenFolders.Add(rel))
                entries.Add(new VfsEntry(rel, true, 0, DateTimeOffset.UtcNow));
        }

        return entries.OrderBy(e => !e.IsDir).ThenBy(e => e.Name).ToList();
    }

    private async Task<VfsEntry?> FindEntryAsync(DataSource ds, string subPath)
    {
        var files = await _fileStorage.ListFilesAsync(ds);

        // Check for exact file match
        foreach (var f in files)
        {
            if (string.Equals(f.Path, subPath, StringComparison.OrdinalIgnoreCase))
                return new VfsEntry(Path.GetFileName(subPath), false, f.StoredSize, f.Modified ?? DateTimeOffset.UtcNow);
        }

        // Check for directory prefix
        var dirPrefix = subPath.EndsWith('/') ? subPath : subPath + "/";
        if (files.Any(f => f.Path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)))
            return new VfsEntry(Path.GetFileName(subPath.TrimEnd('/')), true, 0, DateTimeOffset.UtcNow);

        // Check session-tracked empty directories
        if (_sessionDirs.Contains((ds.Id, dirPrefix)))
            return new VfsEntry(Path.GetFileName(subPath.TrimEnd('/')), true, 0, DateTimeOffset.UtcNow);

        return null;
    }

    #endregion

    #region Binary protocol helpers

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".") return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == "..") { if (stack.Count > 0) stack.Pop(); }
            else if (seg != ".") stack.Push(seg);
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

    private static byte[] DirAttrs(DateTimeOffset modified) =>
        WriteAttrsBytes(ATTR_PERMS | ATTR_ACMODTIME, 0, DIR_MODE, modified);

    private static byte[] FileAttrs(long size, DateTimeOffset modified) =>
        WriteAttrsBytes(ATTR_SIZE | ATTR_PERMS | ATTR_ACMODTIME, size, FILE_MODE, modified);

    private static byte[] WriteAttrsBytes(uint flags, long size, uint perms, DateTimeOffset modified)
    {
        using var ms = new MemoryStream();
        var w = new SftpWriter(ms);
        w.WriteUInt32(flags);
        if ((flags & ATTR_SIZE) != 0) w.WriteUInt64((ulong)size);
        if ((flags & ATTR_PERMS) != 0) w.WriteUInt32(perms);
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
            w.WriteString(""); // language tag
        }));
    }

    private static byte[] BuildHandlePacket(uint requestId, string handle)
    {
        return BuildPacket(SSH_FXP_HANDLE, w => { w.WriteUInt32(requestId); w.WriteString(handle); });
    }

    private static byte[] BuildNamePacket(uint requestId, string name, byte[] attrs)
    {
        return BuildPacket(SSH_FXP_NAME, w =>
        {
            w.WriteUInt32(requestId);
            w.WriteUInt32(1);
            w.WriteString(name);
            w.WriteString(name);
            w.WriteRaw(attrs);
        });
    }

    private static byte[] BuildAttrsPacket(uint requestId, byte[] attrs)
    {
        return BuildPacket(SSH_FXP_ATTRS, w => { w.WriteUInt32(requestId); w.WriteRaw(attrs); });
    }

    private static byte[] BuildPacket(byte type, Action<SftpWriter> write)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[4]); // placeholder for length
        ms.WriteByte(type);
        var w = new SftpWriter(ms);
        write(w);
        var length = (int)ms.Length - 4;
        ms.Position = 0;
        var lenBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)length);
        ms.Write(lenBytes);
        return ms.ToArray();
    }

    #endregion

    #region SftpReader / SftpWriter

    private ref struct SftpReader
    {
        private ReadOnlySpan<byte> _data;
        public SftpReader(ReadOnlySpan<byte> data) => _data = data;
        public int Remaining => _data.Length;
        public uint ReadUInt32() { var v = BinaryPrimitives.ReadUInt32BigEndian(_data); _data = _data[4..]; return v; }
        public ulong ReadUInt64() { var v = BinaryPrimitives.ReadUInt64BigEndian(_data); _data = _data[8..]; return v; }
        public string ReadString() { var len = (int)ReadUInt32(); var s = Encoding.UTF8.GetString(_data[..len]); _data = _data[len..]; return s; }
        public byte[] ReadBytes() { var len = (int)ReadUInt32(); var b = _data[..len].ToArray(); _data = _data[len..]; return b; }
        public void SkipAttrs()
        {
            if (_data.Length < 4) return;
            var flags = ReadUInt32();
            if ((flags & ATTR_SIZE) != 0 && _data.Length >= 8) _data = _data[8..];
            if ((flags & ATTR_UIDGID) != 0 && _data.Length >= 8) _data = _data[8..];
            if ((flags & ATTR_PERMS) != 0 && _data.Length >= 4) _data = _data[4..];
            if ((flags & ATTR_ACMODTIME) != 0 && _data.Length >= 8) _data = _data[8..];
        }
    }

    private sealed class SftpWriter(Stream stream)
    {
        public void WriteUInt32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); stream.Write(b); }
        public void WriteUInt64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); stream.Write(b); }
        public void WriteString(string s) { var bytes = Encoding.UTF8.GetBytes(s); WriteUInt32((uint)bytes.Length); stream.Write(bytes); }
        public void WriteBytes(ReadOnlySpan<byte> data) { WriteUInt32((uint)data.Length); stream.Write(data); }
        public void WriteRaw(ReadOnlySpan<byte> data) => stream.Write(data);
        public void WriteAttrs(byte[] attrs) => stream.Write(attrs);
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        foreach (var h in _handles.Values)
        {
            switch (h)
            {
                case ReadHandle rh: rh.Stream.Dispose(); break;
                case WriteHandle wh: wh.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); break;
            }
        }
        _handles.Clear();
        scope.Dispose();
        onDisposed?.Invoke();
    }
}
