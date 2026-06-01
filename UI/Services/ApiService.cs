using Refit;
using Shared.Contracts;
using Shared.Contracts.Interfaces;
using Shared.Models;

namespace UI.Services;

public sealed class ApiService(
    IAuthApi authApi,
    IUsersApi usersApi,
    IProfileApi profileApi,
    IDataSourcesApi dataSourcesApi,
    IFilesApi filesApi,
    IGlobalConfigApi globalConfigApi,
    ITicketsApi ticketsApi,
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
    public Task<BulkOperationResult> DecryptDataSourceAsync(Guid id) => dataSourcesApi.DecryptAllAsync(id);
    public Task<BulkOperationResult> ReEncryptDataSourceAsync(Guid id, EncryptionMethod method) => dataSourcesApi.ReEncryptAllAsync(id, method);

    public Task<DirectoryListingDto> GetFilesAsync(Guid dataSourceId, string path = "")
        => filesApi.GetAllAsync(dataSourceId, path);

    public async Task<FileEntryDto> UploadFileAsync(Guid dataSourceId, string path, Stream fileStream, string fileName, string contentType)
    {
        var streamPart = new StreamPart(fileStream, fileName, contentType);
        return await filesApi.UploadAsync(dataSourceId, path, streamPart);
    }

    public Task DeleteFileAsync(Guid id) => filesApi.DeleteAsync(id);
    public Task DeleteFolderAsync(Guid dataSourceId, string path) => filesApi.DeleteFolderAsync(dataSourceId, path);
    public Task<FileEntryDto> DecryptFileAsync(Guid id) => filesApi.DecryptAsync(id);
    public Task<FileEntryDto> ReEncryptFileAsync(Guid id, EncryptionMethod method) => filesApi.ReEncryptAsync(id, method);

    public Task<GlobalConfigModel> GetGlobalConfigAsync() => globalConfigApi.GetAsync();
    public Task SaveGlobalConfigAsync(GlobalConfigModel config) => globalConfigApi.SaveAsync(config);

    public Task<List<AccessTicketDto>> GetTicketsAsync() => ticketsApi.GetAllAsync();
    public Task<AccessTicketCreatedDto> CreateTicketAsync(CreateAccessTicketRequest req) => ticketsApi.CreateAsync(req);
    public Task DeleteTicketAsync(Guid id) => ticketsApi.DeleteAsync(id);
    public Task<AccessTicketDto> ExtendTicketAsync(Guid id, ExtendAccessTicketRequest req) => ticketsApi.ExtendAsync(id, req);
}
