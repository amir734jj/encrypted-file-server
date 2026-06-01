using Shared.Models;

namespace Shared.Contracts;

public record CreateDataSourceRequest(
    string Name,
    BackendRequest Backend,
    List<FrontendRequest> Frontends);

public record BackendRequest(
    string Host = "",
    int Port = 21,
    string Username = "",
    string Password = "",
    string BasePath = "/",
    bool UseSsl = false,
    EncryptionMethod EncryptionMethod = EncryptionMethod.AesCtr256);

public record FrontendRequest(
    FrontendType Type,
    bool AllowAnonymous = false);
