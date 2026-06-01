using System.Security.Cryptography;
using Api.Data.Entities;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Shared.Contracts;

namespace Api.Services;

public sealed class DataSourceService(IEfRepository repository, IFileStorageService fileStorage) : IDataSourceService
{
    private IBasicCrud<DataSource> Dal => repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();

    public async Task<List<DataSourceDto>> GetAllAsync(Guid userId)
    {
        var dataSources = (await Dal.GetAll(
            filterExprs: [d => d.UserId == userId],
            orderBy: d => d.Name,
            project: d => d)).ToList();

        var result = new List<DataSourceDto>();
        foreach (var d in dataSources)
        {
            var files = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == d.Id],
                project: f => f)).ToList();
            result.Add(ToDto(d, files.Sum(f => f.OriginalFileSize), files.Count));
        }
        return result;
    }

    public async Task<DataSourceDto?> GetByIdAsync(Guid id, Guid userId)
    {
        var dataSources = (await Dal.GetAll(
            filterExprs: [d => d.Id == id && d.UserId == userId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0) return null;

        var d = dataSources.First();
        var files = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == d.Id],
            project: f => f)).ToList();
        return ToDto(d, files.Sum(f => f.OriginalFileSize), files.Count);
    }

    public async Task<DataSourceDto> CreateAsync(Guid userId, CreateDataSourceRequest req)
    {
        var ds = await Dal.Save(new DataSource
        {
            UserId = userId,
            Name = req.Name.Trim(),
            EncryptionMethod = req.EncryptionMethod,
            BackendFtpHost = req.BackendFtpHost,
            BackendFtpPort = req.BackendFtpPort,
            BackendFtpUsername = req.BackendFtpUsername,
            BackendFtpPassword = req.BackendFtpPassword,
            BackendFtpBasePath = req.BackendFtpBasePath,
            BackendFtpUseSsl = req.BackendFtpUseSsl,
            FrontendFtpEnabled = req.FrontendFtpEnabled,
            FrontendFtpAllowAnonymous = req.FrontendFtpAllowAnonymous,
            FrontendFtpPassword = req.FrontendFtpEnabled ? GeneratePassword() : null,
            FrontendHttpEnabled = req.FrontendHttpEnabled,
            FrontendHttpAllowAnonymous = req.FrontendHttpAllowAnonymous,
            FrontendHttpPassword = req.FrontendHttpEnabled ? GeneratePassword() : null,
        });

        return ToDto(ds, 0, 0);
    }

    public async Task<bool> UpdateAsync(Guid id, Guid userId, UpdateDataSourceRequest req)
    {
        if (!await Dal.Any(filterExprs: [d => d.Id == id && d.UserId == userId]))
            return false;

        await Dal.Update(id, ds =>
        {
            ds.Name = req.Name.Trim();
            ds.EncryptionMethod = req.EncryptionMethod;
            ds.BackendFtpHost = req.BackendFtpHost;
            ds.BackendFtpPort = req.BackendFtpPort;
            ds.BackendFtpUsername = req.BackendFtpUsername;
            ds.BackendFtpPassword = req.BackendFtpPassword;
            ds.BackendFtpBasePath = req.BackendFtpBasePath;
            ds.BackendFtpUseSsl = req.BackendFtpUseSsl;

            if (req.FrontendFtpEnabled && !ds.FrontendFtpEnabled)
                ds.FrontendFtpPassword ??= GeneratePassword();
            ds.FrontendFtpEnabled = req.FrontendFtpEnabled;
            ds.FrontendFtpAllowAnonymous = req.FrontendFtpAllowAnonymous;

            if (req.FrontendHttpEnabled && !ds.FrontendHttpEnabled)
                ds.FrontendHttpPassword ??= GeneratePassword();
            ds.FrontendHttpEnabled = req.FrontendHttpEnabled;
            ds.FrontendHttpAllowAnonymous = req.FrontendHttpAllowAnonymous;
        });

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid userId)
    {
        if (!await Dal.Any(filterExprs: [d => d.Id == id && d.UserId == userId]))
            return false;

        var files = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == id],
            project: f => f)).ToList();
        foreach (var file in files)
            await fileStorage.DeleteFileAsync(file);

        await Dal.Delete(id);
        return true;
    }

    public async Task<bool> ExistsByNameAsync(Guid userId, string name)
    {
        return await Dal.Any(filterExprs: [d => d.UserId == userId && d.Name.ToLower() == name.ToLower().Trim()]);
    }

    private static DataSourceDto ToDto(DataSource d, long totalSize, int fileCount) =>
        new(d.Id, d.Name, d.EncryptionMethod, totalSize, fileCount, d.CreatedAt,
            d.BackendFtpHost, d.BackendFtpPort, d.BackendFtpUsername, d.BackendFtpBasePath, d.BackendFtpUseSsl,
            d.FrontendFtpEnabled, d.FrontendFtpPassword, d.FrontendFtpAllowAnonymous,
            d.FrontendHttpEnabled, d.FrontendHttpPassword, d.FrontendHttpAllowAnonymous);

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes);
    }
}
