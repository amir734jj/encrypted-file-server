namespace Shared.Contracts;

public record RemoteBrowseResponse(
    string Path,
    List<RemoteEntryDto> Entries);

public record RemoteEntryDto(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified);
