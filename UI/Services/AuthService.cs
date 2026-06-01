using Blazored.LocalStorage;
using Shared;

namespace UI.Services;

public sealed class AuthService(ILocalStorageService storage)
{
    private const string TokenKey = "auth_token";
    private const string RoleKey = "auth_role";
    private const string UserIdKey = "auth_userid";
    private const string DisplayNameKey = "auth_displayname";

    public string? Token { get; private set; }
    public string? Role { get; private set; }
    public Guid? UserId { get; private set; }
    public string? DisplayName { get; private set; }

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

    public async Task ClearTokenAsync()
    {
        Token = null;
        Role = null;
        UserId = null;
        DisplayName = null;
        AuthStateChanged?.Invoke();
        await storage.RemoveItemAsync(TokenKey);
        await storage.RemoveItemAsync(RoleKey);
        await storage.RemoveItemAsync(UserIdKey);
        await storage.RemoveItemAsync(DisplayNameKey);
    }
}
