namespace DevChronicle.Models;

public static class SummarizationBatchStatuses
{
    public const string Submitting = "Submitting";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Applying = "Applying";
    public const string Completed = "Completed";
    public const string PartialFailure = "PartialFailure";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";

    public static bool IsTerminal(string status) =>
        string.Equals(status, Completed, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, PartialFailure, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Canceled, StringComparison.OrdinalIgnoreCase);
}

public static class SummarizationBatchItemStatuses
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public class SummarizationBatch
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string OpenAiBatchId { get; set; } = string.Empty;
    public string Status { get; set; } = SummarizationBatchStatuses.Submitting;
    public string? InputFileId { get; set; }
    public string? OutputFileId { get; set; }
    public string? ErrorFileId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? LastError { get; set; }
}

public class SummarizationBatchItem
{
    public int BatchId { get; set; }
    public int SessionId { get; set; }
    public DateTime Day { get; set; }
    public string CustomId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = "v1";
    public string InputHash { get; set; } = string.Empty;
    public int MaxBullets { get; set; }
    public string Status { get; set; } = SummarizationBatchItemStatuses.Pending;
    public string? Error { get; set; }
}
