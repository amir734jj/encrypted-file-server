using Shared.Models;

namespace Shared.Contracts;

public record BackendDto(
    BackendStorageType Protocol,
    string Host,
    int Port,
    string Username,
    string BasePath,
    bool UseSsl,
    EncryptionMethod EncryptionMethod);