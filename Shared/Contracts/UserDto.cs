namespace Shared.Contracts;

public record UserDto(Guid Id, string Email, bool IsActive, List<string> Roles, DateTimeOffset? LastLoginAt);
