namespace Shared.Contracts;

public record RemoteImportRequest(
    RemoteConnectionRequest Connection,
    string RemotePath = "/",
    string TargetPath = "");
