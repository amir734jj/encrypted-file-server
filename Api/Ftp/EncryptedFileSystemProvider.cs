using System.Security.Claims;
using Api.Data;
using Api.Data.Entities;
using Api.Interfaces;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shared.Interfaces;

namespace Api.Ftp;

/// <summary>
/// Creates a virtual encrypted filesystem per authenticated FTP user.
/// Directory structure: / → DataSource dirs → encrypted files (served decrypted).
/// </summary>
public sealed class EncryptedFileSystemProvider(IServiceScopeFactory scopeFactory) : IFileSystemClassFactory
{
    public async Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var userId = accountInformation.FtpUser.GetUserId();
        var scope = scopeFactory.CreateScope();
        return new EncryptedUnixFileSystem(scope, userId);
    }
}

/// <summary>
/// Virtual filesystem backed by encrypted storage. Files appear decrypted to FTP clients.
/// </summary>
public sealed class EncryptedUnixFileSystem : IUnixFileSystem
{
    private readonly IServiceScope _scope;
    private readonly Guid _userId;
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IEncryptionProvider _encryption;
    private readonly UserManager<User> _userManager;

    public EncryptedUnixFileSystem(IServiceScope scope, Guid userId)
    {
        _scope = scope;
        _userId = userId;
        _db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        _encryption = scope.ServiceProvider.GetRequiredService<IEncryptionProvider>();
        _userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        Root = new VirtualDirectoryEntry("/", null);
    }

    private byte[]? _masterKey;
    private async Task<byte[]> GetMasterKeyAsync()
    {
        if (_masterKey is not null) return _masterKey;
        var user = await _userManager.FindByIdAsync(_userId.ToString());
        _masterKey = Convert.FromBase64String(user!.MasterKeyBase64);
        return _masterKey;
    }

    private string DecryptFileName(EncryptedFile f, byte[] masterKey)
    {
        var iv = Convert.FromBase64String(f.IvBase64);
        return _encryption.DecryptString(f.OriginalFileName, masterKey, iv);
    }

    public bool SupportsNonEmptyDirectoryDelete => true;
    public bool SupportsAppend => false;
    public StringComparer FileSystemEntryComparer => StringComparer.OrdinalIgnoreCase;
    public IUnixDirectoryEntry Root { get; }

    public async Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry, CancellationToken ct)
    {
        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: null })
        {
            // Root directory: list data sources as subdirectories
            var dataSources = await _db.DataSources
                .Where(d => d.UserId == _userId)
                .OrderBy(d => d.Name)
                .ToListAsync(ct);

            return dataSources
                .Select(ds => (IUnixFileSystemEntry)new VirtualDirectoryEntry(ds.Name, ds.Id))
                .ToList();
        }

        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: { } dsId })
        {
            // Data source directory: list files (decrypt names in memory)
            var files = await _db.EncryptedFiles
                .Where(f => f.DataSourceId == dsId && f.UserId == _userId)
                .ToListAsync(ct);

            var masterKey = await GetMasterKeyAsync();
            return files
                .Select(f => (IUnixFileSystemEntry)new VirtualFileEntry(f, DecryptFileName(f, masterKey)))
                .OrderBy(e => e.Name)
                .ToList();
        }

        return [];
    }

    public async Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry, string name, CancellationToken ct)
    {
        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: null })
        {
            var ds = await _db.DataSources
                .FirstOrDefaultAsync(d => d.UserId == _userId && d.Name == name, ct);
            return ds is null ? null : new VirtualDirectoryEntry(ds.Name, ds.Id);
        }

        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: { } dsId })
        {
            // File names are encrypted — load all, decrypt, then match
            var files = await _db.EncryptedFiles
                .Where(f => f.DataSourceId == dsId && f.UserId == _userId)
                .ToListAsync(ct);

            var masterKey = await GetMasterKeyAsync();
            var match = files.FirstOrDefault(f => DecryptFileName(f, masterKey) == name);
            return match is null ? null : new VirtualFileEntry(match, name);
        }

        return null;
    }

    public async Task<Stream> OpenReadAsync(IUnixFileEntry fileEntry, long startPosition, CancellationToken ct)
    {
        if (fileEntry is not VirtualFileEntry vfe)
            throw new InvalidOperationException("Unknown file entry type.");

        var user = await _userManager.FindByIdAsync(_userId.ToString());
        var masterKey = Convert.FromBase64String(user!.MasterKeyBase64);
        var stream = await _fileStorage.OpenDecryptedStreamAsync(vfe.EncryptedFile, masterKey);
        _masterKey = masterKey;

        if (startPosition > 0)
        {
            // For resume support, skip bytes. Not ideal for encrypted streams but functional.
            var buffer = new byte[8192];
            long remaining = startPosition;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read == 0) break;
                remaining -= read;
            }
        }

        return stream;
    }

    public async Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry targetDirectory, string fileName, Stream data, CancellationToken ct)
    {
        if (targetDirectory is not VirtualDirectoryEntry { DataSourceId: { } dsId })
            throw new InvalidOperationException("Cannot create files in the root directory. Use a data source folder.");

        var dsOwned = await _db.DataSources.AnyAsync(d => d.Id == dsId && d.UserId == _userId, ct);
        if (!dsOwned)
            throw new UnauthorizedAccessException("Data source does not belong to the current user.");

        await _fileStorage.StoreFileAsync(_userId, dsId, fileName, null, data);
        return null;
    }

    public async Task<IBackgroundTransfer?> ReplaceAsync(IUnixFileEntry fileEntry, Stream data, CancellationToken ct)
    {
        if (fileEntry is not VirtualFileEntry vfe)
            throw new InvalidOperationException("Unknown file entry type.");

        if (vfe.EncryptedFile.UserId != _userId)
            throw new UnauthorizedAccessException("File does not belong to the current user.");

        var dsId = vfe.EncryptedFile.DataSourceId;
        var fileName = vfe.DecryptedName;
        await _fileStorage.DeleteFileAsync(vfe.EncryptedFile);
        await _fileStorage.StoreFileAsync(_userId, dsId, fileName, null, data);
        return null;
    }

    public async Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken ct)
    {
        if (entry is VirtualFileEntry vfe)
        {
            if (vfe.EncryptedFile.UserId != _userId)
                throw new UnauthorizedAccessException("File does not belong to the current user.");

            await _fileStorage.DeleteFileAsync(vfe.EncryptedFile);
        }
        else if (entry is VirtualDirectoryEntry { DataSourceId: { } dsId })
        {
            var ds = await _db.DataSources.FirstOrDefaultAsync(
                d => d.Id == dsId && d.UserId == _userId, ct);
            if (ds is null) return;

            var files = await _db.EncryptedFiles
                .Where(f => f.DataSourceId == dsId && f.UserId == _userId)
                .ToListAsync(ct);
            foreach (var f in files)
                await _fileStorage.DeleteFileAsync(f);

            _db.DataSources.Remove(ds);
            await _db.SaveChangesAsync(ct);
        }
    }

    public Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory, string directoryName, CancellationToken ct)
    {
        throw new NotSupportedException("Data sources cannot be created via FTP. Use the web interface to configure backend storage.");
    }

    public Task<IUnixFileSystemEntry> MoveAsync(IUnixDirectoryEntry parent, IUnixFileSystemEntry source,
        IUnixDirectoryEntry target, string fileName, CancellationToken ct)
    {
        throw new NotSupportedException("Move is not supported.");
    }

    public Task<IBackgroundTransfer?> AppendAsync(IUnixFileEntry fileEntry, long? startPosition, Stream data, CancellationToken ct)
    {
        throw new NotSupportedException("Append is not supported for encrypted files.");
    }

    public Task<IUnixFileSystemEntry> SetMacTimeAsync(IUnixFileSystemEntry entry,
        DateTimeOffset? modify, DateTimeOffset? access, DateTimeOffset? create, CancellationToken ct)
    {
        return Task.FromResult(entry);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }
}

/// <summary>
/// Virtual directory representing either the root or a data source folder.
/// </summary>
public sealed class VirtualDirectoryEntry(string name, Guid? dataSourceId) : IUnixDirectoryEntry
{
    public Guid? DataSourceId => dataSourceId;
    public string Name => name;
    public IUnixPermissions Permissions => new VirtualPermissions();
    public DateTimeOffset? LastWriteTime => DateTimeOffset.UtcNow;
    public DateTimeOffset? CreatedTime => DateTimeOffset.UtcNow;
    public long NumberOfLinks => 1;
    public string Owner => "owner";
    public string Group => "group";
    public bool IsDeletable => dataSourceId is not null; // Can't delete root
    public bool IsRoot => dataSourceId is null && name == "/";
}

/// <summary>
/// Virtual file entry backed by an encrypted file.
/// </summary>
public sealed class VirtualFileEntry(EncryptedFile encryptedFile, string decryptedName) : IUnixFileEntry
{
    public EncryptedFile EncryptedFile => encryptedFile;
    public string DecryptedName => decryptedName;
    public string Name => decryptedName;
    public IUnixPermissions Permissions => new VirtualPermissions();
    public DateTimeOffset? LastWriteTime => encryptedFile.UpdatedAt ?? encryptedFile.CreatedAt;
    public DateTimeOffset? CreatedTime => encryptedFile.CreatedAt;
    public long NumberOfLinks => 1;
    public string Owner => "owner";
    public string Group => "group";
    public long Size => encryptedFile.OriginalFileSize;
}

public sealed class VirtualPermissions : IUnixPermissions
{
    public IAccessMode User => new FullAccess();
    public IAccessMode Group => new ReadOnlyAccess();
    public IAccessMode Other => new ReadOnlyAccess();
}

public sealed class FullAccess : IAccessMode
{
    public bool Read => true;
    public bool Write => true;
    public bool Execute => true;
}

public sealed class ReadOnlyAccess : IAccessMode
{
    public bool Read => true;
    public bool Write => false;
    public bool Execute => false;
}
