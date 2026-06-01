using System.Security.Cryptography;
using Api.Data.Entities;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Shared.Contracts;
using Shared.Models;

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
            Backend = new BackendConfig
            {
                Host = req.Backend.Host,
                Port = req.Backend.Port,
                Username = req.Backend.Username,
                Password = req.Backend.Password,
                BasePath = req.Backend.BasePath,
                UseSsl = req.Backend.UseSsl,
                EncryptionMethod = req.Backend.EncryptionMethod,
            },
            Frontends = req.Frontends.Select(f => new FrontendConfig
            {
                Type = f.Type,
                AllowAnonymous = f.AllowAnonymous,
                Password = GeneratePassword(),
            }).ToList(),
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
            ds.Backend.Host = req.Backend.Host;
            ds.Backend.Port = req.Backend.Port;
            ds.Backend.Username = req.Backend.Username;
            ds.Backend.Password = req.Backend.Password;
            ds.Backend.BasePath = req.Backend.BasePath;
            ds.Backend.UseSsl = req.Backend.UseSsl;
            ds.Backend.EncryptionMethod = req.Backend.EncryptionMethod;

            // Sync frontends: add new, update existing, remove absent
            var requestedTypes = req.Frontends.Select(f => f.Type).ToHashSet();

            // Remove frontends that are no longer requested
            ds.Frontends.RemoveAll(f => !requestedTypes.Contains(f.Type));

            foreach (var fr in req.Frontends)
            {
                var existing = ds.GetFrontend(fr.Type);
                if (existing is not null)
                {
                    existing.AllowAnonymous = fr.AllowAnonymous;
                }
                else
                {
                    ds.Frontends.Add(new FrontendConfig
                    {
                        Type = fr.Type,
                        AllowAnonymous = fr.AllowAnonymous,
                        Password = GeneratePassword(),
                    });
                }
            }
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
        new(d.Id, d.Name, totalSize, fileCount, d.CreatedAt,
            new BackendDto(d.Backend.Host, d.Backend.Port, d.Backend.Username,
                d.Backend.BasePath, d.Backend.UseSsl, d.Backend.EncryptionMethod),
            d.Frontends.Select(f => new FrontendDto(f.Type, f.Password, f.AllowAnonymous)).ToList());

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes);
    }
}
