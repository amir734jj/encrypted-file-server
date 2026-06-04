using Refit;
using Shared.Contracts;
using Shared.Contracts.Interfaces;
using Shared.Models;
using System.Net;
using Newtonsoft.Json;

namespace UI.Services;

public sealed class ApiService(
    IAuthApi authApi,
    IUsersApi usersApi,
    IProfileApi profileApi,
    IDataSourcesApi dataSourcesApi,
    IFilesApi filesApi,
    IGlobalConfigApi globalConfigApi,
    ITicketsApi ticketsApi,
    IRemoteImportApi remoteImportApi,
    AuthService auth)
{
    public async Task<string?> LoginAsync(string email, string password)
    {
        try
        {
            var response = await authApi.LoginAsync(new LoginRequest(email, password));
            await auth.SetTokenAsync(response.Token, response.Role, response.UserId);
            try
            {
                var me = await authApi.MeAsync();
                await auth.SetDisplayNameAsync(me.DisplayName);
            }
            catch { /* non-critical */ }
            return null;
        }
        catch (ApiException ex) when ((int)ex.StatusCode == 403)
        {
            return "Your account is pending activation by an administrator.";
        }
        catch (ApiException)
        {
            return "Invalid email or password.";
        }
    }

    public async Task<(bool Success, bool IsActive, string? Error)> RegisterAsync(
        string email, string password, string passwordConfirm)
    {
        try
        {
            var response = await authApi.RegisterAsync(new RegisterRequest(email, password, passwordConfirm));
            return (true, response.IsActive, null);
        }
        catch (ApiException)
        {
            return (false, false, "Registration failed. Password must be at least 8 characters.");
        }
    }

    public async Task<MeResponse> GetProfileAsync() => await authApi.MeAsync();

    public async Task ImpersonateAsync(Guid userId)
    {
        var response = await authApi.ImpersonateAsync(userId);
        await auth.StartImpersonatingAsync(response.Token, response.Role, response.UserId);
        try
        {
            var me = await authApi.MeAsync();
            await auth.SetDisplayNameAsync(me.DisplayName);
        }
        catch { /* non-critical */ }
    }

    public async Task StopImpersonatingAsync()
    {
        await auth.StopImpersonatingAsync();
    }

    public Task<List<UserDto>> GetUsersAsync() => usersApi.GetAllAsync();
    public Task ActivateUserAsync(Guid id) => usersApi.ActivateAsync(id);
    public Task DeactivateUserAsync(Guid id) => usersApi.DeactivateAsync(id);
    public Task DeleteUserAsync(Guid id) => usersApi.DeleteAsync(id);

    public Task UpdateProfileAsync(UpdateProfileRequest req) => profileApi.UpdateAsync(req);
    public Task ChangePasswordAsync(ChangePasswordRequest req) => profileApi.ChangePasswordAsync(req);

    public Task<List<DataSourceDto>> GetDataSourcesAsync() => dataSourcesApi.GetAllAsync();
    public Task<DataSourceDto> CreateDataSourceAsync(CreateDataSourceRequest req) => dataSourcesApi.CreateAsync(req);
    public Task UpdateDataSourceAsync(Guid id, UpdateDataSourceRequest req) => dataSourcesApi.UpdateAsync(id, req);
    public Task DeleteDataSourceAsync(Guid id) => dataSourcesApi.DeleteAsync(id);
    public Task<MasterPasswordResponse> GetMasterPasswordAsync(Guid id) => dataSourcesApi.GetMasterPasswordAsync(id);
    public Task<DataSourceCredentialsResponse> GetCredentialsAsync(Guid id) => dataSourcesApi.GetCredentialsAsync(id);

    public Task<DirectoryListingDto> GetFilesAsync(Guid dataSourceId, string path = "")
        => filesApi.GetAllAsync(dataSourceId, path);

    public async Task<FileEntryDto> UploadFileAsync(Guid dataSourceId, string path, Stream fileStream, string fileName, string contentType)
    {
        var streamPart = new StreamPart(fileStream, fileName, contentType);
        return await filesApi.UploadAsync(dataSourceId, path, streamPart);
    }

    public Task DeleteFileAsync(Guid dataSourceId, string path) => filesApi.DeleteAsync(dataSourceId, path);
    public Task DeleteFolderAsync(Guid dataSourceId, string path) => filesApi.DeleteFolderAsync(dataSourceId, path);
    public Task MoveFolderAsync(Guid dataSourceId, string sourcePath, string destinationPath) => filesApi.MoveFolderAsync(dataSourceId, sourcePath, destinationPath);

    public Task<GlobalConfigModel> GetGlobalConfigAsync() => globalConfigApi.GetAsync();
    public Task SaveGlobalConfigAsync(GlobalConfigModel config) => globalConfigApi.SaveAsync(config);

    public Task<List<AccessTicketDto>> GetTicketsAsync() => ticketsApi.GetAllAsync();
    public Task<AccessTicketCreatedDto> CreateTicketAsync(CreateAccessTicketRequest req) => ticketsApi.CreateAsync(req);
    public Task DeleteTicketAsync(Guid id) => ticketsApi.DeleteAsync(id);
    public Task<AccessTicketDto> ExtendTicketAsync(Guid id, ExtendAccessTicketRequest req) => ticketsApi.ExtendAsync(id, req);

    // Remote import
    public Task<RemoteBrowseResponse> BrowseRemoteAsync(RemoteBrowseRequest req) => remoteImportApi.BrowseAsync(req);
    public Task StartImportRemoteAsync(Guid dataSourceId, RemoteImportRequest req) => remoteImportApi.ImportAsync(dataSourceId, req);

    /// <summary>
    /// Polls the import progress endpoint. Returns either a BulkOperationProgress (still running)
    /// or a RemoteImportResult (completed), or null (no import running).
    /// </summary>
    public async Task<(BulkOperationProgress? Progress, RemoteImportResult? Result)> GetImportStatusAsync(Guid dataSourceId)
    {
        using var response = await remoteImportApi.GetImportProgressRawAsync(dataSourceId);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content is null)
            return (null, null);

        var json = response.Content;

        // If it has "Imported" field, it's a completed result; otherwise it's progress
        if (json.Contains("\"imported\"", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("\"failed\"", StringComparison.OrdinalIgnoreCase))
        {
            var result = JsonConvert.DeserializeObject<RemoteImportResult>(json);
            return (null, result);
        }

        var progress = JsonConvert.DeserializeObject<BulkOperationProgress>(json);
        return (progress, null);
    }
}
