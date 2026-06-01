namespace Api.ViewModels;

public sealed class EntryViewModel
{
    public required string Name { get; init; }
    public required string Href { get; init; }
    public string? RawHref { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
}