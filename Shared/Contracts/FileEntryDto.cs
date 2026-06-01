namespace Shared.Contracts;

public record FileEntryDto(
    Guid Id,
    Guid DataSourceId,
    string FileName,
    string? ContentType,
    long FileSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
