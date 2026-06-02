using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services.Backend;
using EfCoreRepository.Interfaces;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Interfaces;
using Shared.Models;

namespace Api.Ftp;

public sealed class EncryptedUnixFileSystem(IServiceScope scope, Guid? userId) : IUnixFileSystem
{
    private readonly IEfRepository _repository = scope.ServiceProvider.GetRequiredService<IEfRepository>();
    private readonly IFileStorageService _fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
    private readonly IEncryptionProviderFactory _encryptionFactory = scope.ServiceProvider.GetRequiredService<IEncryptionProviderFactory>();
    private readonly IBackendStorageProviderFactory _backendStorageFactory = scope.ServiceProvider.GetRequiredService<IBackendStorageProviderFactory>();
    private readonly Dictionary<Guid, byte[]> _masterKeyCache = new();
    private readonly HashSet<(Guid dsId, string virtualPath)> _sessionDirs = [];

    private IBasicCrud<DataSource> DataSourceDal => _repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => _repository.For<EncryptedFile>();

    private bool IsAnonymous => !userId.HasValue;

    private async Task<byte[]> GetMasterKeyForDataSourceAsync(Guid dataSourceId)
    {
        if (_masterKeyCache.TryGetValue(dataSourceId, out var cached))
        {
            return cached;
        }

        var ds = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).First();
        var key = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        _masterKeyCache[dataSourceId] = key;
        return key;
    }

    private async Task<IEncryptionProvider> GetEncryptionForDataSourceAsync(Guid dataSourceId)
    {
        var ds = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).First();
        return _encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
    }

    private string DecryptFileName(EncryptedFile f, byte[] masterKey, IEncryptionProvider defaultEncryption)
    {
        var encryption = f.EncryptionMethod.HasValue
            ? _encryptionFactory.GetProvider(f.EncryptionMethod.Value)
            : defaultEncryption;
        var iv = Convert.FromBase64String(f.IvBase64);
        return encryption.DecryptString(f.OriginalFileName, masterKey, iv);
    }

    private async Task<IEnumerable<EncryptedFile>> GetFilesForDataSourceAsync(Guid dsId)
    {
        return userId.HasValue
            ? await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == dsId, f => f.UserId == userId.Value],
                project: f => f)
            : await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == dsId],
                project: f => f);
    }

    private void EnsureAuthenticated()
    {
        if (IsAnonymous)
        {
            throw new UnauthorizedAccessException("Anonymous users cannot modify files.");
        }
    }

    public bool SupportsNonEmptyDirectoryDelete => true;
    public bool SupportsAppend => false;
    public StringComparer FileSystemEntryComparer => StringComparer.OrdinalIgnoreCase;
    public IUnixDirectoryEntry Root { get; } = new VirtualDirectoryEntry("/", null, null);

    public async Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry, CancellationToken ct)
    {
        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: null })
        {
            var dataSources = IsAnonymous
                ? (await DataSourceDal.GetAll(
                    filterExprs: [d => d.Frontends.Any(f => f.Type == FrontendType.Ftp && f.AllowAnonymous)],
                    project: d => d)).OrderBy(d => d.Name).ToList()
                : (await DataSourceDal.GetAll(
                    filterExprs: [d => d.UserId == userId && d.Frontends.Any(f => f.Type == FrontendType.Ftp)],
                    project: d => d)).OrderBy(d => d.Name).ToList();

            return dataSources
                .Select(IUnixFileSystemEntry (ds) => new VirtualDirectoryEntry(ds.Name, ds.Id, ""))
                .ToList();
        }

        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            var currentPath = vde.VirtualPath ?? "";

            var files = (await GetFilesForDataSourceAsync(dsId)).ToList();

            var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
            var encryption = await GetEncryptionForDataSourceAsync(dsId);
            var results = new List<IUnixFileSystemEntry>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                string fullPath;
                try
                {
                    fullPath = DecryptFileName(f, masterKey, encryption);
                }
                catch
                {
                    continue; // Skip files that can't be decrypted
                }

                if (!fullPath.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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
                    {
                        results.Add(new VirtualDirectoryEntry(folderName, dsId, currentPath + folderName + "/"));
                    }
                }
            }

            // Include session-tracked empty directories
            foreach (var (sid, vpath) in _sessionDirs)
            {
                if (sid != dsId || !vpath.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase) || vpath == currentPath)
                    continue;
                var rel = vpath[currentPath.Length..].TrimEnd('/');
                if (!rel.Contains('/') && seenFolders.Add(rel))
                {
                    results.Add(new VirtualDirectoryEntry(rel, dsId, vpath));
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
                ? (await DataSourceDal.GetAll(
                    filterExprs: [d => d.Frontends.Any(f => f.Type == FrontendType.Ftp && f.AllowAnonymous) && d.Name == name],
                    project: d => d, maxResults: 1)).FirstOrDefault()
                : (await DataSourceDal.GetAll(
                    filterExprs: [d => d.UserId == userId && d.Frontends.Any(f => f.Type == FrontendType.Ftp) && d.Name == name],
                    project: d => d, maxResults: 1)).FirstOrDefault();
            return ds is null ? null : new VirtualDirectoryEntry(ds.Name, ds.Id, "");
        }

        if (directoryEntry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            var currentPath = vde.VirtualPath ?? "";

            var files = (await GetFilesForDataSourceAsync(dsId)).ToList();

            var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
            var encryption = await GetEncryptionForDataSourceAsync(dsId);
            foreach (var f in files)
            {
                string fullPath;
                try
                {
                    fullPath = DecryptFileName(f, masterKey, encryption);
                }
                catch
                {
                    continue;
                }

                if (!fullPath.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = fullPath[currentPath.Length..];
                var slashIndex = relativePath.IndexOf('/');

                if (slashIndex < 0 && string.Equals(relativePath, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new VirtualFileEntry(f, relativePath);
                }

                if (slashIndex >= 0)
                {
                    var folderName = relativePath[..slashIndex];
                    if (string.Equals(folderName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new VirtualDirectoryEntry(folderName, dsId, currentPath + folderName + "/");
                    }
                }
            }

            // Check session-tracked dirs (from explicit MKD)
            var candidatePath = currentPath + name + "/";
            if (_sessionDirs.Contains((dsId, candidatePath)))
            {
                return new VirtualDirectoryEntry(name, dsId, candidatePath);
            }

            // Return null for unknown names — this ensures STOR for new files
            // works correctly. Use MKD to create directories before CWD.
        }

        return null;
    }

    public async Task<Stream> OpenReadAsync(IUnixFileEntry fileEntry, long startPosition, CancellationToken ct)
    {
        if (fileEntry is not VirtualFileEntry vfe)
        {
            throw new InvalidOperationException("Unknown file entry type.");
        }

        var masterKey = await GetMasterKeyForDataSourceAsync(vfe.EncryptedFile.DataSourceId);
        var stream = await _fileStorage.OpenDecryptedStreamAsync(vfe.EncryptedFile);

        if (startPosition > 0)
        {
            var buffer = new byte[8192];
            long remaining = startPosition;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read == 0)
                {
                    break;
                }

                remaining -= read;
            }
        }

        return stream;
    }

    public async Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry targetDirectory, string fileName, Stream data, CancellationToken ct)
    {
        EnsureAuthenticated();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<EncryptedUnixFileSystem>();

        if (targetDirectory is not VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            throw new InvalidOperationException("Cannot create files in the root directory. Use a data source folder.");
        }

        logger.LogInformation("FTP CreateAsync: dsId={DsId}, userId={UserId}, virtualPath={VPath}, fileName={FName}",
            dsId, userId, vde.VirtualPath, fileName);

        var dsOwned = await DataSourceDal.Any(filterExprs: [d => d.Id == dsId && d.UserId == userId]);
        if (!dsOwned)
        {
            logger.LogError("FTP CreateAsync: DataSource {DsId} not owned by user {UserId}", dsId, userId);
            throw new UnauthorizedAccessException("Data source does not belong to the current user.");
        }

        var fullPath = (vde.VirtualPath ?? "") + fileName;
        var mime = InferContentType(fileName);
        logger.LogInformation("FTP CreateAsync: Storing file fullPath={FullPath}, mime={Mime}", fullPath, mime);
        try
        {
            await _fileStorage.StoreFileAsync(userId!.Value, dsId, fullPath, mime, data);
            logger.LogInformation("FTP CreateAsync: StoreFileAsync completed successfully for {FullPath}", fullPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FTP CreateAsync: StoreFileAsync FAILED for {FullPath}", fullPath);
            throw;
        }
        return null;
    }

    public async Task<IBackgroundTransfer?> ReplaceAsync(IUnixFileEntry fileEntry, Stream data, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (fileEntry is not VirtualFileEntry vfe)
        {
            throw new InvalidOperationException("Unknown file entry type.");
        }

        if (vfe.EncryptedFile.UserId != userId)
        {
            throw new UnauthorizedAccessException("File does not belong to the current user.");
        }

        var dsId = vfe.EncryptedFile.DataSourceId;
        var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
        var encryption = await GetEncryptionForDataSourceAsync(dsId);
        var fullPath = DecryptFileName(vfe.EncryptedFile, masterKey, encryption);
        await _fileStorage.DeleteFileAsync(vfe.EncryptedFile);
        var mime = InferContentType(fullPath);
        await _fileStorage.StoreFileAsync(userId!.Value, dsId, fullPath, mime, data);
        return null;
    }

    public async Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (entry is VirtualFileEntry vfe)
        {
            if (vfe.EncryptedFile.UserId != userId)
            {
                throw new UnauthorizedAccessException("File does not belong to the current user.");
            }

            await _fileStorage.DeleteFileAsync(vfe.EncryptedFile);
        }
        else if (entry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            if (string.IsNullOrEmpty(vde.VirtualPath))
            {
                var ds = (await DataSourceDal.GetAll(
                    filterExprs: [d => d.Id == dsId && d.UserId == userId],
                    project: d => d, maxResults: 1)).FirstOrDefault();
                if (ds is null)
                {
                    return;
                }

                var allFiles = (await FileDal.GetAll(
                    filterExprs: [f => f.DataSourceId == dsId && f.UserId == userId],
                    project: f => f)).ToList();
                foreach (var f in allFiles)
                    await _fileStorage.DeleteFileAsync(f);

                await DataSourceDal.Delete(ds.Id);
            }
            else
            {
                var masterKey = await GetMasterKeyForDataSourceAsync(dsId);
                var encryption = await GetEncryptionForDataSourceAsync(dsId);
                var allFiles = (await FileDal.GetAll(
                    filterExprs: [f => f.DataSourceId == dsId && f.UserId == userId],
                    project: f => f)).ToList();

                foreach (var f in allFiles)
                {
                    var fullPath = DecryptFileName(f, masterKey, encryption);
                    if (fullPath.StartsWith(vde.VirtualPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await _fileStorage.DeleteFileAsync(f);
                    }
                }

                // Remove this directory and any children from session tracking
                _sessionDirs.RemoveWhere(e => e.dsId == dsId &&
                    e.virtualPath.StartsWith(vde.VirtualPath, StringComparison.OrdinalIgnoreCase));
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
            _sessionDirs.Add((dsId, newPath));
            return Task.FromResult<IUnixDirectoryEntry>(new VirtualDirectoryEntry(directoryName, dsId, newPath));
        }

        throw new NotSupportedException("Data sources cannot be created via FTP. Use the web interface to configure backend storage.");
    }

    public async Task<IUnixFileSystemEntry> MoveAsync(IUnixDirectoryEntry parent, IUnixFileSystemEntry source,
        IUnixDirectoryEntry target, string fileName, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (target is not VirtualDirectoryEntry { DataSourceId: { } targetDsId } targetVde)
        {
            throw new InvalidOperationException("Cannot move to root directory.");
        }

        var targetPath = targetVde.VirtualPath ?? "";

        if (source is VirtualFileEntry vfe)
        {
            if (vfe.EncryptedFile.UserId != userId)
            {
                throw new UnauthorizedAccessException("File does not belong to the current user.");
            }

            if (vfe.EncryptedFile.DataSourceId != targetDsId)
            {
                throw new NotSupportedException("Cannot move files between data sources.");
            }

            var masterKey = await GetMasterKeyForDataSourceAsync(targetDsId);
            var encryption = await GetEncryptionForDataSourceAsync(targetDsId);
            var iv = Convert.FromBase64String(vfe.EncryptedFile.IvBase64);
            var newFullPath = targetPath + fileName;
            var encryptedName = encryption.EncryptString(newFullPath, masterKey, iv);

            // For None encryption, also rename the actual file on the backend
            var ds = (await DataSourceDal.GetAll(
                filterExprs: [d => d.Id == targetDsId],
                project: d => d, maxResults: 1)).First();
            string? newStoragePath = null;
            if (ds.Backend.EncryptionMethod == EncryptionMethod.None)
            {
                var connection = ds.ToBackendConnectionInfo();
                newStoragePath = await _backendStorageFactory.GetProvider(ds.Backend.Protocol).RenameAsync(connection, vfe.EncryptedFile.StoragePath, newFullPath, ct);
            }

            var capturedEncryptedName = encryptedName;
            var capturedStoragePath = newStoragePath;
            await FileDal.Update(vfe.EncryptedFile.Id, (Action<EncryptedFile>)(existing =>
            {
                existing.OriginalFileName = capturedEncryptedName;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                if (capturedStoragePath is not null)
                {
                    existing.StoragePath = capturedStoragePath;
                }
            }));

            return new VirtualFileEntry(vfe.EncryptedFile, fileName);
        }

        if (source is VirtualDirectoryEntry { DataSourceId: { } sourceDsId } sourceVde)
        {
            if (sourceDsId != targetDsId)
            {
                throw new NotSupportedException("Cannot move directories between data sources.");
            }

            var oldPrefix = sourceVde.VirtualPath ?? "";
            if (string.IsNullOrEmpty(oldPrefix))
            {
                throw new NotSupportedException("Cannot move a data source root via FTP.");
            }

            var newPrefix = targetPath + fileName + "/";
            var masterKey = await GetMasterKeyForDataSourceAsync(sourceDsId);
            var encryption = await GetEncryptionForDataSourceAsync(sourceDsId);

            var allFiles = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == sourceDsId && f.UserId == userId],
                project: f => f)).ToList();

            var ds = (await DataSourceDal.GetAll(
                filterExprs: [d => d.Id == sourceDsId],
                project: d => d, maxResults: 1)).First();

            foreach (var f in allFiles)
            {
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);
                if (!fullPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var newFullPath = newPrefix + fullPath[oldPrefix.Length..];
                var encryptedName = encryption.EncryptString(newFullPath, masterKey, iv);

                string? newStoragePath = null;
                // For None encryption, also rename the actual file on the backend
                if (ds.Backend.EncryptionMethod == EncryptionMethod.None)
                {
                    var connection = ds.ToBackendConnectionInfo();
                    newStoragePath = await _backendStorageFactory.GetProvider(ds.Backend.Protocol).RenameAsync(connection, f.StoragePath, newFullPath, ct);
                }

                var capturedEncryptedName = encryptedName;
                var capturedStoragePath = newStoragePath;
                await FileDal.Update(f.Id, (Action<EncryptedFile>)(existing =>
                {
                    existing.OriginalFileName = capturedEncryptedName;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    if (capturedStoragePath is not null)
                    {
                        existing.StoragePath = capturedStoragePath;
                    }
                }));
            }

            // Update session-tracked dirs
            var oldDirs = _sessionDirs.Where(e => e.dsId == sourceDsId &&
                e.virtualPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var old in oldDirs)
            {
                _sessionDirs.Remove(old);
                _sessionDirs.Add((sourceDsId, newPrefix + old.virtualPath[oldPrefix.Length..]));
            }

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

    private static string? InferContentType(string fileName)
    {
        return new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var ct) ? ct : null;
    }

    public void Dispose()
    {
        scope.Dispose();
    }
}