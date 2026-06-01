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
using Shared.Models;

namespace Api.Ftp;

/// <summary>
/// Creates a virtual encrypted filesystem per authenticated FTP user.
/// Directory structure: / → DataSource dirs → encrypted files (served decrypted).
/// </summary>
public sealed class EncryptedFileSystemProvider(IServiceScopeFactory scopeFactory) : IFileSystemClassFactory
{
    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var scope = scopeFactory.CreateScope();

        if (accountInformation.FtpUser.IsAnonymous())
            return Task.FromResult<IUnixFileSystem>(new EncryptedUnixFileSystem(scope, userId: null));

        var userId = accountInformation.FtpUser.GetUserId();
        return Task.FromResult<IUnixFileSystem>(new EncryptedUnixFileSystem(scope, userId));
    }
}

public sealed class EncryptedUnixFileSystem : IUnixFileSystem
{
    private readonly IServiceScope _scope;
    private readonly Guid? _userId;
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IEncryptionProviderFactory _encryptionFactory;
    private readonly UserManager<User> _userManager;
    private readonly Dictionary<Guid, byte[]> _masterKeyCache = new();

    public EncryptedUnixFileSystem(IServiceScope scope, Guid? userId)
    {
        _scope = scope;
        _userId = userId;
        _db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        _encryptionFactory = scope.ServiceProvider.GetRequiredService<IEncryptionProviderFactory>();
        _userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        Root = new VirtualDirectoryEntry("/", null, null);
    }

    private bool IsAnonymous => !_userId.HasValue;

    private async Task<byte[]> GetMasterKeyAsync(Guid ownerId)
    {
        if (_masterKeyCache.TryGetValue(ownerId, out var cached))
            return cached;
        var user = await _userManager.FindByIdAsync(ownerId.ToString());
        var key = Convert.FromBase64String(user!.MasterKeyBase64);
        _masterKeyCache[ownerId] = key;
        return key;
    }

    private async Task<byte[]> GetMasterKeyForDataSourceAsync(Guid dataSourceId)
    {
        var ds = await _db.DataSources.FirstAsync(d => d.Id == dataSourceId);
        return await GetMasterKeyAsync(ds.UserId);
    }

    private async Task<IEncryptionProvider> GetEncryptionForDataSourceAsync(Guid dataSourceId)
    {
        var ds = await _db.DataSources.FirstAsync(d => d.Id == dataSourceId);
        return _encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
    }

    private string DecryptFileName(EncryptedFile f, byte[] masterKey, IEncryptionProvider encryption)
    {
        var iv = Convert.FromBase64String(f.IvBase64);
        return encryption.DecryptString(f.OriginalFileName, masterKey, iv);
    }

    private void EnsureAuthenticated()
    {
        if (IsAnonymous)
            throw new UnauthorizedAccessException("Anonymous users cannot modify files.");
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
            var dataSources = IsAnonymous
                ? await _db.DataSources
                    .Where(d => d.Frontends.Any(f => f.Type == FrontendType.Ftp && f.AllowAnonymous))
                    .OrderBy(d => d.Name).ToListAsync(ct)
                : await _db.DataSources
                    .Where(d => d.UserId == _userId && d.Frontends.Any(f => f.Type == FrontendType.Ftp))
                    .OrderBy(d => d.Name).ToListAsync(ct);

            return dataSources
                .Select(ds => (IUnixFileSystemEntry)new VirtualDirectoryEntry(ds.Name, ds.Id, ""))
                .ToList();
        }

        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            var currentPath = vde.VirtualPath ?? "";

            var query = _db.EncryptedFiles.Where(f => f.DataSourceId == dsId);
            if (_userId.HasValue)
                query = query.Where(f => f.UserId == _userId.Value);
            var files = await query.ToListAsync(ct);

            var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
            var encryption = await GetEncryptionForDataSourceAsync(dsId);
            var results = new List<IUnixFileSystemEntry>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                var fullPath = DecryptFileName(f, masterKey, encryption);
                if (!fullPath.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = fullPath[currentPath.Length..];
                var slashIndex = relativePath.IndexOf('/');

                if (slashIndex < 0)
                {
                    results.Add(new VirtualFileEntry(f, relativePath));
                }
                else
                {
                    var folderName = relativePath[..slashIndex];
                    if (seenFolders.Add(folderName))
                        results.Add(new VirtualDirectoryEntry(folderName, dsId, currentPath + folderName + "/"));
                }
            }

            return results.OrderBy(e => e is VirtualDirectoryEntry ? 0 : 1)
                .ThenBy(e => e.Name).ToList();
        }

        return [];
    }

    public async Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry, string name, CancellationToken ct)
    {
        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: null })
        {
            var ds = IsAnonymous
                ? await _db.DataSources.FirstOrDefaultAsync(
                    d => d.Frontends.Any(f => f.Type == FrontendType.Ftp && f.AllowAnonymous) && d.Name == name, ct)
                : await _db.DataSources.FirstOrDefaultAsync(
                    d => d.UserId == _userId && d.Frontends.Any(f => f.Type == FrontendType.Ftp) && d.Name == name, ct);
            return ds is null ? null : new VirtualDirectoryEntry(ds.Name, ds.Id, "");
        }

        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            var currentPath = vde.VirtualPath ?? "";

            var query = _db.EncryptedFiles.Where(f => f.DataSourceId == dsId);
            if (_userId.HasValue)
                query = query.Where(f => f.UserId == _userId.Value);
            var files = await query.ToListAsync(ct);

            var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
            var encryption = await GetEncryptionForDataSourceAsync(dsId);
            foreach (var f in files)
            {
                var fullPath = DecryptFileName(f, masterKey, encryption);
                if (!fullPath.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = fullPath[currentPath.Length..];
                var slashIndex = relativePath.IndexOf('/');

                if (slashIndex < 0 && string.Equals(relativePath, name, StringComparison.OrdinalIgnoreCase))
                    return new VirtualFileEntry(f, relativePath);

                if (slashIndex >= 0)
                {
                    var folderName = relativePath[..slashIndex];
                    if (string.Equals(folderName, name, StringComparison.OrdinalIgnoreCase))
                        return new VirtualDirectoryEntry(folderName, dsId, currentPath + folderName + "/");
                }
            }
        }

        return null;
    }

    public async Task<Stream> OpenReadAsync(IUnixFileEntry fileEntry, long startPosition, CancellationToken ct)
    {
        if (fileEntry is not VirtualFileEntry vfe)
            throw new InvalidOperationException("Unknown file entry type.");

        var masterKey = await GetMasterKeyForDataSourceAsync(vfe.EncryptedFile.DataSourceId);
        var stream = await _fileStorage.OpenDecryptedStreamAsync(vfe.EncryptedFile, masterKey);

        if (startPosition > 0)
        {
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
        EnsureAuthenticated();

        if (targetDirectory is not VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
            throw new InvalidOperationException("Cannot create files in the root directory. Use a data source folder.");

        var dsOwned = await _db.DataSources.AnyAsync(d => d.Id == dsId && d.UserId == _userId, ct);
        if (!dsOwned)
            throw new UnauthorizedAccessException("Data source does not belong to the current user.");

        var fullPath = (vde.VirtualPath ?? "") + fileName;
        await _fileStorage.StoreFileAsync(_userId!.Value, dsId, fullPath, null, data);
        return null;
    }

    public async Task<IBackgroundTransfer?> ReplaceAsync(IUnixFileEntry fileEntry, Stream data, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (fileEntry is not VirtualFileEntry vfe)
            throw new InvalidOperationException("Unknown file entry type.");

        if (vfe.EncryptedFile.UserId != _userId)
            throw new UnauthorizedAccessException("File does not belong to the current user.");

        var dsId = vfe.EncryptedFile.DataSourceId;
        var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
        var encryption = await GetEncryptionForDataSourceAsync(dsId);
        var fullPath = DecryptFileName(vfe.EncryptedFile, masterKey, encryption);
        await _fileStorage.DeleteFileAsync(vfe.EncryptedFile);
        await _fileStorage.StoreFileAsync(_userId!.Value, dsId, fullPath, null, data);
        return null;
    }

    public async Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (entry is VirtualFileEntry vfe)
        {
            if (vfe.EncryptedFile.UserId != _userId)
                throw new UnauthorizedAccessException("File does not belong to the current user.");

            await _fileStorage.DeleteFileAsync(vfe.EncryptedFile);
        }
        else if (entry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            if (string.IsNullOrEmpty(vde.VirtualPath))
            {
                var ds = await _db.DataSources.FirstOrDefaultAsync(
                    d => d.Id == dsId && d.UserId == _userId, ct);
                if (ds is null) return;

                var allFiles = await _db.EncryptedFiles
                    .Where(f => f.DataSourceId == dsId && f.UserId == _userId)
                    .ToListAsync(ct);
                foreach (var f in allFiles)
                    await _fileStorage.DeleteFileAsync(f);

                _db.DataSources.Remove(ds);
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
                var encryption = await GetEncryptionForDataSourceAsync(dsId);
                var allFiles = await _db.EncryptedFiles
                    .Where(f => f.DataSourceId == dsId && f.UserId == _userId)
                    .ToListAsync(ct);

                foreach (var f in allFiles)
                {
                    var fullPath = DecryptFileName(f, masterKey, encryption);
                    if (fullPath.StartsWith(vde.VirtualPath, StringComparison.OrdinalIgnoreCase))
                        await _fileStorage.DeleteFileAsync(f);
                }
            }
        }
    }

    public Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory, string directoryName, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (targetDirectory is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            var newPath = (vde.VirtualPath ?? "") + directoryName + "/";
            return Task.FromResult<IUnixDirectoryEntry>(new VirtualDirectoryEntry(directoryName, dsId, newPath));
        }

        throw new NotSupportedException("Data sources cannot be created via FTP. Use the web interface to configure backend storage.");
    }

    public async Task<IUnixFileSystemEntry> MoveAsync(IUnixDirectoryEntry parent, IUnixFileSystemEntry source,
        IUnixDirectoryEntry target, string fileName, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (target is not VirtualDirectoryEntry { DataSourceId: { } targetDsId } targetVde)
            throw new InvalidOperationException("Cannot move to root directory.");

        var targetPath = targetVde.VirtualPath ?? "";

        if (source is VirtualFileEntry vfe)
        {
            if (vfe.EncryptedFile.UserId != _userId)
                throw new UnauthorizedAccessException("File does not belong to the current user.");
            if (vfe.EncryptedFile.DataSourceId != targetDsId)
                throw new NotSupportedException("Cannot move files between data sources.");

            var masterKey = await GetMasterKeyForDataSourceAsync(targetDsId);
            var encryption = await GetEncryptionForDataSourceAsync(targetDsId);
            var iv = Convert.FromBase64String(vfe.EncryptedFile.IvBase64);
            var newFullPath = targetPath + fileName;
            var encryptedName = encryption.EncryptString(newFullPath, masterKey, iv);

            vfe.EncryptedFile.OriginalFileName = encryptedName;
            vfe.EncryptedFile.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new VirtualFileEntry(vfe.EncryptedFile, fileName);
        }

        if (source is VirtualDirectoryEntry { DataSourceId: { } sourceDsId } sourceVde)
        {
            if (sourceDsId != targetDsId)
                throw new NotSupportedException("Cannot move directories between data sources.");

            var oldPrefix = sourceVde.VirtualPath ?? "";
            if (string.IsNullOrEmpty(oldPrefix))
                throw new NotSupportedException("Cannot move a data source root via FTP.");

            var newPrefix = targetPath + fileName + "/";
            var masterKey = await GetMasterKeyForDataSourceAsync(sourceDsId);
            var encryption = await GetEncryptionForDataSourceAsync(sourceDsId);

            var allFiles = await _db.EncryptedFiles
                .Where(f => f.DataSourceId == sourceDsId && f.UserId == _userId)
                .ToListAsync(ct);

            foreach (var f in allFiles)
            {
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);
                if (!fullPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var newFullPath = newPrefix + fullPath[oldPrefix.Length..];
                f.OriginalFileName = encryption.EncryptString(newFullPath, masterKey, iv);
                f.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            return new VirtualDirectoryEntry(fileName, targetDsId, newPrefix);
        }

        throw new InvalidOperationException("Unknown entry type.");
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
public sealed class VirtualDirectoryEntry(string name, Guid? dataSourceId, string? virtualPath) : IUnixDirectoryEntry
{
    public Guid? DataSourceId => dataSourceId;
    public string? VirtualPath => virtualPath;
    public string Name => name;
    public IUnixPermissions Permissions => new VirtualPermissions();
    public DateTimeOffset? LastWriteTime => DateTimeOffset.UtcNow;
    public DateTimeOffset? CreatedTime => DateTimeOffset.UtcNow;
    public long NumberOfLinks => 1;
    public string Owner => "owner";
    public string Group => "group";
    public bool IsDeletable => dataSourceId is not null;
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
