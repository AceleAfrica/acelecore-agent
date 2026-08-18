using AceleCoreAgent.Core;

namespace AceleCoreAgent.Watcher;

/// <summary>
/// Watches a single root folder recursively.
/// Groups files by immediate subfolder (the batch folder).
/// Fires BatchReady when a batch folder has been idle for StabilitySeconds
/// and contains at least MinFiles files newer than StartFrom.
/// </summary>
public class BatchDetector : IDisposable
{
    private readonly AppSettings _settings;
    private FileSystemWatcher? _watcher;

    // FolderPath -> idle timer
    private readonly Dictionary<string, System.Timers.Timer> _folderTimers = new();
    // FolderPath -> files seen
    private readonly Dictionary<string, HashSet<string>> _folderFiles = new();
    private readonly object _lock = new();

    public event EventHandler<BatchReadyEventArgs>? BatchReady;

    public BatchDetector(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (!Directory.Exists(_settings.WatchFolder))
        {
            Logger.Log($"Watch folder does not exist: {_settings.WatchFolder}", Logger.LogLevel.Error);
            return;
        }

        _watcher = new FileSystemWatcher(_settings.WatchFolder)
        {
            Filter = "*.xlsx",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Created += OnFileCreated;
        _watcher.Error += OnWatcherError;

        Logger.Log($"Watching: {_settings.WatchFolder}", Logger.LogLevel.Info);
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_lock)
        {
            foreach (var timer in _folderTimers.Values)
                timer.Dispose();
            _folderTimers.Clear();
            _folderFiles.Clear();
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        // Skip temp files
        if (Path.GetFileName(e.FullPath).StartsWith("~$")) return;
        if (!e.FullPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return;

        // Only process files newer than the configured start time
        try
        {
            var fileDate = File.GetLastWriteTime(e.FullPath);
            if (fileDate < _settings.StartFromDateTime)
            {
                Logger.Log($"Skipping old file: {e.Name}", Logger.LogLevel.Info);
                return;
            }
        }
        catch { return; }

        // The batch folder is the immediate parent of the file
        var batchFolder = Path.GetDirectoryName(e.FullPath)!;

        lock (_lock)
        {
            if (!_folderFiles.ContainsKey(batchFolder))
                _folderFiles[batchFolder] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _folderFiles[batchFolder].Add(e.FullPath);

            // Reset or create idle timer for this folder
            if (_folderTimers.TryGetValue(batchFolder, out var existingTimer))
            {
                existingTimer.Stop();
                existingTimer.Start();
            }
            else
            {
                var timer = new System.Timers.Timer(_settings.BatchStabilitySeconds * 1000)
                {
                    AutoReset = false
                };
                timer.Elapsed += (s, ev) => OnFolderIdle(batchFolder);
                timer.Start();
                _folderTimers[batchFolder] = timer;
            }

            Logger.Log($"File detected: {Path.GetFileName(e.FullPath)} in {Path.GetFileName(batchFolder)}", Logger.LogLevel.Info);
        }
    }

    private void OnFolderIdle(string batchFolder)
    {
        List<string> files;

        lock (_lock)
        {
            if (!_folderFiles.TryGetValue(batchFolder, out var fileSet))
            {
                Logger.Log($"OnFolderIdle: no files found for {batchFolder}", Logger.LogLevel.Warning);
                return;
            }

            // Capture files BEFORE removing from dictionary
            files = fileSet.ToList();

            // Now clean up
            if (_folderTimers.TryGetValue(batchFolder, out var timer))
            {
                timer.Dispose();
                _folderTimers.Remove(batchFolder);
            }
            _folderFiles.Remove(batchFolder);
        }

        Logger.Log($"OnFolderIdle fired for {batchFolder} — {files.Count} files captured", Logger.LogLevel.Info);

        // Filter to only files newer than start date that actually exist
        files = files.Where(f =>
        {
            try
            {
                return File.Exists(f) &&
                       File.GetLastWriteTime(f) >= _settings.StartFromDateTime;
            }
            catch { return false; }
        }).ToList();

        Logger.Log($"After date filter: {files.Count} files remain", Logger.LogLevel.Info);

        if (files.Count < _settings.MinFilesPerBatch)
        {
            Logger.Log($"Folder {Path.GetFileName(batchFolder)} has only {files.Count} new files — below minimum {_settings.MinFilesPerBatch}, skipping", Logger.LogLevel.Warning);
            return;
        }

        var batchLabel = BuildBatchLabel(batchFolder);
        Logger.Log($"Batch ready: {batchLabel} ({files.Count} files)", Logger.LogLevel.Success);

        BatchReady?.Invoke(this, new BatchReadyEventArgs
        {
            FolderPath = batchFolder,
            BatchLabel = batchLabel,
            Files = files,
            DetectedAt = DateTime.Now,
        });
    }

    private string BuildBatchLabel(string folderPath)
    {
        var relative = Path.GetRelativePath(_settings.WatchFolder, folderPath);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        var label = string.Join("__", parts
            .Select(p => p.ToUpperInvariant()
                .Replace(" ", "-")
                .Replace(".", "")
                .Replace("(", "")
                .Replace(")", "")
                .Trim('-'))
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        if (string.IsNullOrWhiteSpace(label)) label = "BATCH";
        return $"{label}_{DateTime.Now:MMMM_yyyy}".ToUpperInvariant();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Logger.Log($"FileSystemWatcher error: {e.GetException()?.Message}", Logger.LogLevel.Error);
        // Restart watcher after a brief delay
        Task.Delay(5000).ContinueWith(_ =>
        {
            try { Stop(); Start(); }
            catch (Exception ex)
            {
                Logger.Log($"Failed to restart watcher: {ex.Message}", Logger.LogLevel.Error);
            }
        });
    }

    public void Dispose() => Stop();
}