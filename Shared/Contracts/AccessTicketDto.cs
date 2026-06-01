namespace Shared.Contracts;

public record AccessTicketDto(
    Guid Id,
    string Username,
    string Password,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public record AccessTicketCreatedDto(
    Guid Id,
    string Username,
    string Password,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public record CreateAccessTicketRequest(DateTimeOffset ExpiresAt);

public record ExtendAccessTicketRequest(DateTimeOffset ExpiresAt);
