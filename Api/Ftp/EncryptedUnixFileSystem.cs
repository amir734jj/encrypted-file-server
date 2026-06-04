using Api.Data.Entities;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Models;

namespace Api.Ftp;

public sealed class EncryptedUnixFileSystem(IServiceScope scope, Guid? userId) : IUnixFileSystem
{
    private readonly IFileStorageService _fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
    private readonly IEfRepository _repository = scope.ServiceProvider.GetRequiredService<IEfRepository>();
    private readonly HashSet<(Guid dsId, string virtualPath)> _sessionDirs = [];

    private IBasicCrud<DataSource> DataSourceDal => _repository.For<DataSource>();

    private bool IsAnonymous => !userId.HasValue;

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
            var ds = await GetDataSourceAsync(dsId);
            if (ds is null) return [];

            var backendFiles = await _fileStorage.ListFilesAsync(ds, ct);
            var results = new List<IUnixFileSystemEntry>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in backendFiles)
            {
                if (!string.IsNullOrEmpty(currentPath) &&
                    !f.Path.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = string.IsNullOrEmpty(currentPath) ? f.Path : f.Path[currentPath.Length..];
                var slashIndex = relativePath.IndexOf('/');

                if (slashIndex < 0)
                {
                    results.Add(new VirtualFileEntry(dsId, f.Path, relativePath, f.StoredSize, f.Modified));
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
            var ds = await GetDataSourceAsync(dsId);
            if (ds is null) return null;

            var backendFiles = await _fileStorage.ListFilesAsync(ds, ct);
            foreach (var f in backendFiles)
            {
                if (!string.IsNullOrEmpty(currentPath) &&
                    !f.Path.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = string.IsNullOrEmpty(currentPath) ? f.Path : f.Path[currentPath.Length..];
                var slashIndex = relativePath.IndexOf('/');

                if (slashIndex < 0 && string.Equals(relativePath, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new VirtualFileEntry(dsId, f.Path, relativePath, f.StoredSize, f.Modified);
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

            // Check session-tracked dirs
            var candidatePath = currentPath + name + "/";
            if (_sessionDirs.Contains((dsId, candidatePath)))
            {
                return new VirtualDirectoryEntry(name, dsId, candidatePath);
            }
        }

        return null;
    }

    public async Task<Stream> OpenReadAsync(IUnixFileEntry fileEntry, long startPosition, CancellationToken ct)
    {
        if (fileEntry is not VirtualFileEntry vfe)
        {
            throw new InvalidOperationException("Unknown file entry type.");
        }

        var ds = await GetDataSourceAsync(vfe.DataSourceId)
            ?? throw new InvalidOperationException("Data source not found.");

        var stream = await _fileStorage.OpenDecryptedStreamAsync(ds, vfe.RelativePath);

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
        {
            throw new InvalidOperationException("Cannot create files in the root directory. Use a data source folder.");
        }

        var ds = await GetDataSourceAsync(dsId)
            ?? throw new InvalidOperationException("Data source not found.");

        var fullPath = (vde.VirtualPath ?? "") + fileName;
        var mime = InferContentType(fileName);
        await _fileStorage.StoreFileAsync(ds, fullPath, mime, data);
        return null;
    }

    public async Task<IBackgroundTransfer?> ReplaceAsync(IUnixFileEntry fileEntry, Stream data, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (fileEntry is not VirtualFileEntry vfe)
        {
            throw new InvalidOperationException("Unknown file entry type.");
        }

        var ds = await GetDataSourceAsync(vfe.DataSourceId)
            ?? throw new InvalidOperationException("Data source not found.");

        await _fileStorage.DeleteFileAsync(ds, vfe.RelativePath);
        var mime = InferContentType(vfe.RelativePath);
        await _fileStorage.StoreFileAsync(ds, vfe.RelativePath, mime, data);
        return null;
    }

    public async Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken ct)
    {
        EnsureAuthenticated();

        if (entry is VirtualFileEntry vfe)
        {
            var ds = await GetDataSourceAsync(vfe.DataSourceId)
                ?? throw new InvalidOperationException("Data source not found.");
            await _fileStorage.DeleteFileAsync(ds, vfe.RelativePath);
        }
        else if (entry is VirtualDirectoryEntry { DataSourceId: { } dsId } vde)
        {
            var ds = await GetDataSourceAsync(dsId);
            if (ds is null) return;

            if (string.IsNullOrEmpty(vde.VirtualPath))
            {
                // Delete entire data source
                var allFiles = await _fileStorage.ListFilesAsync(ds, ct);
                foreach (var f in allFiles)
                {
                    await _fileStorage.DeleteFileAsync(ds, f.Path);
                }
                await DataSourceDal.Delete(ds.Id);
            }
            else
            {
                // Delete all files under this directory
                var allFiles = await _fileStorage.ListFilesAsync(ds, ct);
                foreach (var f in allFiles)
                {
                    if (f.Path.StartsWith(vde.VirtualPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await _fileStorage.DeleteFileAsync(ds, f.Path);
                    }
                }

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

        throw new NotSupportedException("Data sources cannot be created via FTP.");
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
            if (vfe.DataSourceId != targetDsId)
            {
                throw new NotSupportedException("Cannot move files between data sources.");
            }

            var ds = await GetDataSourceAsync(targetDsId)
                ?? throw new InvalidOperationException("Data source not found.");

            var newFullPath = targetPath + fileName;
            await _fileStorage.RenameFileAsync(ds, vfe.RelativePath, newFullPath);

            return new VirtualFileEntry(targetDsId, newFullPath, fileName, vfe.Size, DateTimeOffset.UtcNow);
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

            var ds = await GetDataSourceAsync(sourceDsId)
                ?? throw new InvalidOperationException("Data source not found.");

            var newPrefix = targetPath + fileName + "/";
            var allFiles = await _fileStorage.ListFilesAsync(ds, ct);

            foreach (var f in allFiles)
            {
                if (!f.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var newPath = newPrefix + f.Path[oldPrefix.Length..];
                await _fileStorage.RenameFileAsync(ds, f.Path, newPath);
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
        throw new NotSupportedException("Append is not supported.");
    }

    public Task<IUnixFileSystemEntry> SetMacTimeAsync(IUnixFileSystemEntry entry,
        DateTimeOffset? modify, DateTimeOffset? access, DateTimeOffset? create, CancellationToken ct)
    {
        return Task.FromResult(entry);
    }

    private async Task<DataSource?> GetDataSourceAsync(Guid dsId)
    {
        return (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dsId],
            project: d => d,
            maxResults: 1)).FirstOrDefault();
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
