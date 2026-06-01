namespace Shared.Contracts;

public record LoginResponse(string Token, string Role, Guid UserId);
