using Api.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Api.Data.Entities;

public sealed class User : IdentityUser<Guid>, IEntity
{
    public bool IsActive { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Base64-encoded 256-bit AES master key for encrypting/decrypting files.
    /// Generated once on registration and never changed.
    /// </summary>
    public string MasterKeyBase64 { get; init; } = string.Empty;
}
