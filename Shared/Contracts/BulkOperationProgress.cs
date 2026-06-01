namespace Shared.Contracts;

public record BulkOperationProgress(string Operation, int TotalFiles, int Processed);
