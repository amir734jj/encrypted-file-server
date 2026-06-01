using Blazored.LocalStorage;
using Shared;

namespace UI.Services;

public sealed class AuthService(ILocalStorageService storage)
{
    private const string TokenKey = "auth_token";
    private const string RoleKey = "auth_role";
    private const string UserIdKey = "auth_userid";
    private const string DisplayNameKey = "auth_displayname";
    private const string OriginalTokenKey = "auth_original_token";
    private const string OriginalRoleKey = "auth_original_role";
    private const string OriginalUserIdKey = "auth_original_userid";
    private const string OriginalDisplayNameKey = "auth_original_displayname";

    public string? Token { get; private set; }
    public string? Role { get; private set; }
    public Guid? UserId { get; private set; }
    public string? DisplayName { get; private set; }
    public bool IsImpersonating { get; private set; }

    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : !string.IsNullOrWhiteSpace(Role) ? Role
        : "there";

    public bool IsAuthenticated => Token is not null;
    public bool IsAdmin => Role == Roles.Admin;

    public event Action? AuthStateChanged;

    public async Task InitAsync()
    {
        Token = await storage.GetItemAsync<string>(TokenKey);
        Role = await storage.GetItemAsync<string>(RoleKey);
        UserId = await storage.GetItemAsync<Guid?>(UserIdKey);
        DisplayName = await storage.GetItemAsync<string>(DisplayNameKey);
        IsImpersonating = await storage.ContainKeyAsync(OriginalTokenKey);
    }

    public async Task SetTokenAsync(string token, string role, Guid userId)
    {
        Token = token;
        Role = role;
        UserId = userId;
        AuthStateChanged?.Invoke();
        await storage.SetItemAsync(TokenKey, token);
        await storage.SetItemAsync(RoleKey, role);
        await storage.SetItemAsync(UserIdKey, userId);
    }

    public async Task SetDisplayNameAsync(string? displayName)
    {
        DisplayName = displayName;
        AuthStateChanged?.Invoke();
        await storage.SetItemAsync(DisplayNameKey, displayName);
    }

    public async Task StartImpersonatingAsync(string token, string role, Guid userId)
    {
        await storage.SetItemAsync(OriginalTokenKey, Token);
        await storage.SetItemAsync(OriginalRoleKey, Role);
        await storage.SetItemAsync(OriginalUserIdKey, UserId);
        await storage.SetItemAsync(OriginalDisplayNameKey, DisplayName);

        Token = token;
        Role = role;
        UserId = userId;
        IsImpersonating = true;
        AuthStateChanged?.Invoke();
        await storage.SetItemAsync(TokenKey, token);
        await storage.SetItemAsync(RoleKey, role);
        await storage.SetItemAsync(UserIdKey, userId);
    }

    public async Task StopImpersonatingAsync()
    {
        Token = await storage.GetItemAsync<string>(OriginalTokenKey);
        Role = await storage.GetItemAsync<string>(OriginalRoleKey);
        UserId = await storage.GetItemAsync<Guid?>(OriginalUserIdKey);
        DisplayName = await storage.GetItemAsync<string>(OriginalDisplayNameKey);
        IsImpersonating = false;

        await storage.SetItemAsync(TokenKey, Token);
        await storage.SetItemAsync(RoleKey, Role);
        await storage.SetItemAsync(UserIdKey, UserId);
        await storage.SetItemAsync(DisplayNameKey, DisplayName);

        await storage.RemoveItemAsync(OriginalTokenKey);
        await storage.RemoveItemAsync(OriginalRoleKey);
        await storage.RemoveItemAsync(OriginalUserIdKey);
        await storage.RemoveItemAsync(OriginalDisplayNameKey);

        AuthStateChanged?.Invoke();
    }

    public async Task ClearTokenAsync()
    {
        Token = null;
        Role = null;
        UserId = null;
        DisplayName = null;
        IsImpersonating = false;
        AuthStateChanged?.Invoke();
        await storage.RemoveItemAsync(TokenKey);
        await storage.RemoveItemAsync(RoleKey);
        await storage.RemoveItemAsync(UserIdKey);
        await storage.RemoveItemAsync(DisplayNameKey);
        await storage.RemoveItemAsync(OriginalTokenKey);
        await storage.RemoveItemAsync(OriginalRoleKey);
        await storage.RemoveItemAsync(OriginalUserIdKey);
        await storage.RemoveItemAsync(OriginalDisplayNameKey);
    }
}
