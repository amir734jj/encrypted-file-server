namespace Shared.Contracts;

public record DirectoryListingDto(
    string CurrentPath,
    List<FolderEntryDto> Folders,
    List<FileEntryDto> Files);
