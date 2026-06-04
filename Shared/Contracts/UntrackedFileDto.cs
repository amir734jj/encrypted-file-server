namespace Shared.Contracts;

public record UntrackedFileDto(
    string StoragePath,
    string FileName,
    long Size,
    DateTimeOffset? Modified);

public record DiscoverResult(List<UntrackedFileDto> Files);

public record AdoptFilesRequest(Guid DataSourceId, List<string> StoragePaths);

public record AdoptFilesResult(int Adopted, int Failed, List<string> Errors);
