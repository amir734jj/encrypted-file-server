namespace Shared.Contracts;

public record FileEntryDto(
    Guid DataSourceId,
    string FileName,
    string? ContentType,
    long FileSize,
    DateTimeOffset? Modified);
