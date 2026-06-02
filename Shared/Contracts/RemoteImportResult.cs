namespace Shared.Contracts;

public record RemoteImportResult(int TotalFiles, int Imported, int Failed, List<string> Errors);
