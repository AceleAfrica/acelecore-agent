namespace AceleCoreAgent.Queue;

public enum BatchStatus
{
    Pending,
    Sending,
    Sent,
    Failed
}

public class BatchQueueItem
{
    public int Id { get; set; }
    public string FolderPath { get; set; } = "";
    public string BatchLabel { get; set; } = "";
    public int FileCount { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public BatchStatus Status { get; set; } = BatchStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public string? Notes { get; set; }
}