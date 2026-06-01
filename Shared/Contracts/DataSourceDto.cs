using Shared.Models;

namespace Shared.Contracts;

public record DataSourceDto(
    Guid Id,
    string Name,
    long TotalFileSize,
    int FileCount,
    DateTimeOffset CreatedAt,
    BackendDto Backend,
    List<FrontendDto> Frontends);

public record BackendDto(
    BackendStorageType Protocol,
    string Host,
    int Port,
    string Username,
    string BasePath,
    bool UseSsl,
    EncryptionMethod EncryptionMethod);

public record FrontendDto(
    FrontendType Type,
    bool AllowAnonymous);
