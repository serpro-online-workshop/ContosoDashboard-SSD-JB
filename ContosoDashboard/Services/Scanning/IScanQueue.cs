namespace ContosoDashboard.Services;

// Contracts for the asynchronous scan pipeline (see specs/001-document-management/contracts/scan-pipeline.md).
// The same records are used by the in-process queue (training) and the Azure Queue Storage implementation (cloud).
public record ScanRequest(int DocumentId, Guid ScanAttemptId, string StorageKey, long SizeBytes, DateTime EnqueuedUtc);

public enum ScanVerdict
{
    Clean,
    Infected,
    Error
}

public record ScanVerdictMessage(int DocumentId, Guid ScanAttemptId, ScanVerdict Verdict, string? Reason, string Scanner, DateTime ScannedUtc);

public interface IScanQueue
{
    Task EnqueueAsync(ScanRequest request, CancellationToken ct = default);
}

// Applies a scan verdict to the document; returns false when the verdict was ignored
// (duplicate, stale attempt, unknown or deleted document).
public interface IScanResultHandler
{
    Task<bool> ApplyVerdictAsync(ScanVerdictMessage verdict, CancellationToken ct = default);
}

// Maintenance operations used by the background workers
public interface IScanMaintenance
{
    Task<int> FailTimedOutScansAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<IReadOnlyList<ScanRequest>> GetPendingScanRequestsAsync(CancellationToken ct = default);
}

public class MalwareScanOptions
{
    public const string SectionName = "MalwareScan";

    public string Mode { get; set; } = "InProcess";
    public string Scanner { get; set; } = "NoOp";
    public int SimulatedDelaySeconds { get; set; }
    public int PendingTimeoutMinutes { get; set; } = 15;
    public string RequestQueueName { get; set; } = "document-scan-requests";
    public string ResultQueueName { get; set; } = "document-scan-results";
    public string QueueServiceUri { get; set; } = string.Empty;
}
