namespace Api.ViewModels;

public sealed class DirectoryListingViewModel
{
    public required string Title { get; init; }
    public required string CurrentPath { get; init; }
    public required List<EntryViewModel> Entries { get; init; }
    public string? ParentHref { get; init; }
    public string? Badge { get; init; }
    public string? Subtitle { get; init; }
    public string NameHeader { get; init; } = "Name";
    public string DateHeader { get; init; } = "Modified";
    public string AccentColor { get; init; } = "#00d4ff";
    public string AccentColorLight { get; init; } = "#0066cc";
}

public sealed class EntryViewModel
{
    public required string Name { get; init; }
    public required string Href { get; init; }
    public string? RawHref { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
}
