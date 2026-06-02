using Shared.Contracts;

namespace Shared.Contracts;

public record RemoteConnectionRequest(
    BackendStorageType Protocol = BackendStorageType.FtpClient,
    string Host = "",
    int Port = 21,
    string Username = "",
    string Password = "",
    string BasePath = "/",
    bool UseSsl = false);
