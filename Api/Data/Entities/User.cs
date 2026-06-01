using Api.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Api.Data.Entities;

public sealed class User : IdentityUser<Guid>, IEntity
{
    public bool IsActive { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? DisplayName { get; set; }
}
