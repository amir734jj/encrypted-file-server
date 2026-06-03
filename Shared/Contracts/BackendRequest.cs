using Shared.Models;

namespace Shared.Contracts;

public record BackendRequest(
    BackendStorageType Protocol = BackendStorageType.FtpClient,
    string Host = "",
    int Port = 21,
    string Username = "",
    string Password = "",
    string BasePath = "/",
    bool UseSsl = false,
    EncryptionMethod EncryptionMethod = EncryptionMethod.AesCtr256,
    string MasterPassword = "",
    bool UseCompression = false);