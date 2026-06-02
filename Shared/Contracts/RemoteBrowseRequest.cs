namespace Shared.Contracts;

public record RemoteBrowseRequest(
    RemoteConnectionRequest Connection,
    string Path = "/");
