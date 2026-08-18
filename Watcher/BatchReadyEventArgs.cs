namespace AceleCoreAgent.Watcher;

public class BatchReadyEventArgs : EventArgs
{
    public string FolderPath { get; init; } = "";
    public string BatchLabel { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public DateTime DetectedAt { get; init; } = DateTime.Now;
}