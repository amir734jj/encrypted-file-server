namespace Shared.Contracts;

public record DataSourceDto(
    Guid Id,
    string Name,
    long TotalFileSize,
    int FileCount,
    DateTimeOffset CreatedAt,
    BackendDto Backend,
    List<FrontendDto> Frontends,
    long? MaxSizeBytes = null);