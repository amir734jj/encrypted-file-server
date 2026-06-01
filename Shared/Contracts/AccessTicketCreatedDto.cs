namespace Shared.Contracts;

public record AccessTicketCreatedDto(
    Guid Id,
    string Username,
    string Password,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);