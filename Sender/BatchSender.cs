using AceleCoreAgent.Core;
using AceleCoreAgent.Queue;
using Newtonsoft.Json.Linq;

namespace AceleCoreAgent.Sender;

public class BatchSendProgress
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public string CurrentFile { get; set; } = "";
}

public class BatchSender
{
    private readonly AppSettings _settings;
    private readonly QueueDatabase _db;
    private readonly ApiClient _api;
    private Dictionary<string, string>? _cellTypeCache;

    public event Action<BatchSendProgress>? ProgressChanged;

    public BatchSender(AppSettings settings, QueueDatabase db, ApiClient api)
    {
        _settings = settings;
        _db = db;
        _api = api;
    }

    public async Task ProcessQueueAsync(CancellationToken ct = default)
    {
        var pending = _db.GetPending();
        if (pending.Count == 0)
        {
            Logger.Log("No pending batches in queue", Logger.LogLevel.Info);
            return;
        }

        Logger.Log($"Processing {pending.Count} queued batch(es)", Logger.LogLevel.Info);

        foreach (var batch in pending)
        {
            if (ct.IsCancellationRequested) break;
            await SendBatchAsync(batch, ct);
        }
    }

    public async Task SendBatchAsync(BatchQueueItem batch, CancellationToken ct = default)
    {
        Logger.Log($"Sending batch: {batch.BatchLabel} from {batch.FolderPath}", Logger.LogLevel.Info);
        _db.UpdateStatus(batch.Id, BatchStatus.Sending);
        _cellTypeCache = null;

        try
        {
            if (!await _api.LoginAsync())
            {
                _db.UpdateStatus(batch.Id, BatchStatus.Failed, "Authentication failed");
                return;
            }

            await PreloadCellTypes();

            var files = Directory.GetFiles(batch.FolderPath, "*.xlsx", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                .Where(f => File.GetLastWriteTime(f) >= _settings.StartFromDateTime)
                .ToList();

            var toProcess = files.Where(f => !_db.IsFileProcessed(f)).ToList();

            Logger.Log($"  {toProcess.Count} new files to process ({files.Count - toProcess.Count} already done)",
                Logger.LogLevel.Info);

            var progress = new BatchSendProgress { Total = toProcess.Count };
            var processedFiles = new List<string>();
            var errors = new List<string>();

            foreach (var filePath in toProcess)
            {
                if (ct.IsCancellationRequested) break;

                progress.CurrentFile = Path.GetFileName(filePath);
                ProgressChanged?.Invoke(progress);

                try
                {
                    var parsed = FileParser.Parse(filePath);
                    if (parsed == null)
                    {
                        Logger.Log($"  ⏭️ Skipped (no barcode): {Path.GetFileName(filePath)}", Logger.LogLevel.Info);
                        progress.Skipped++;
                        progress.Processed++;
                        processedFiles.Add(filePath);
                        continue;
                    }

                    if (parsed.IsPack)
                    {
                        Logger.Log($"  🔋 Pack file skipped: {Path.GetFileName(filePath)}", Logger.LogLevel.Info);
                        progress.Skipped++;
                        progress.Processed++;
                        processedFiles.Add(filePath);
                        continue;
                    }

                    var cell = await FindOrCreateCellAsync(parsed, batch.BatchLabel);
                    if (cell == null)
                    {
                        progress.Errors++;
                        progress.Processed++;
                        errors.Add($"{Path.GetFileName(filePath)}: Could not find or create cell");
                        continue;
                    }

                    var cellId = cell["id"]?.ToString();
                    if (string.IsNullOrEmpty(cellId))
                    {
                        progress.Errors++;
                        progress.Processed++;
                        errors.Add($"{Path.GetFileName(filePath)}: Cell has no ID");
                        continue;
                    }

                    var isDeadCell = parsed.CapacityAh <= 0;
                    var relativeFile = Path.GetRelativePath(_settings.WatchFolder, filePath)
                        .Replace('\\', '/');

                    var payload = new Dictionary<string, object?>
                    {
                        ["cellId"] = cellId,
                        ["testDate"] = parsed.TestDate.ToString("O"),
                        ["capacityAh"] = isDeadCell ? 0.001 : parsed.CapacityAh,
                        ["rawFileName"] = relativeFile,
                        ["performedBy"] = "acecore-agent",
                        ["sessionKey"] = batch.BatchLabel,
                        ["sessionLabel"] = batch.BatchLabel,
                        ["notes"] = isDeadCell
                            ? $"Dead cell | Original: {parsed.OriginalSerial}"
                            : $"Original: {parsed.OriginalSerial}",
                    };

                    // Only add optional fields if they have values
                    if (parsed.EnergyWh.HasValue && parsed.EnergyWh > 0)
                        payload["energyWh"] = parsed.EnergyWh.Value;
                    if (parsed.DcirMohm.HasValue && parsed.DcirMohm > 0)
                        payload["dcirMohm"] = parsed.DcirMohm.Value;
                    if (parsed.OnsetVoltage.HasValue && parsed.OnsetVoltage > 0)
                        payload["onsetVoltage"] = parsed.OnsetVoltage.Value;
                    if (parsed.EndVoltage.HasValue && parsed.EndVoltage > 0)
                        payload["endVoltage"] = parsed.EndVoltage.Value;
                    if (isDeadCell)
                        payload["result"] = "FAIL";

                    var result = await _api.PostAsync("test-records", payload);

                    // Log full response for debugging
                    Logger.Log($"  Test record response: {result?.ToString()?.Substring(0, Math.Min(300, result?.ToString()?.Length ?? 0))}", Logger.LogLevel.Info);

                    var success = result?["success"]?.Value<bool>() ?? false;
                    var message = result?["message"]?.ToString() ?? "";

                    // Check for duplicate
                    if (message.ToLower().Contains("duplicate") ||
                        message.ToLower().Contains("unique") ||
                        message.ToLower().Contains("already"))
                    {
                        Logger.Log($"  ⚠️ Already synced: {Path.GetFileName(filePath)}", Logger.LogLevel.Warning);
                        progress.Skipped++;
                        processedFiles.Add(filePath);
                        progress.Processed++;
                        ProgressChanged?.Invoke(progress);
                        continue;
                    }

                    if (!success)
                    {
                        Logger.Log($"  ❌ Test record failed: {message}", Logger.LogLevel.Error);
                        progress.Errors++;
                        errors.Add($"{Path.GetFileName(filePath)}: {message}");
                        progress.Processed++;
                        ProgressChanged?.Invoke(progress);
                        continue;
                    }

                    // Safe extraction — data may be JObject or null JValue
                    var resultData = result?["data"] as JObject;
                    var recordResult = resultData?["result"]?.ToString() ?? "UNKNOWN";

                    if (recordResult == "PASS") progress.Passed++;
                    else progress.Failed++;

                    Logger.Log($"  ✅ {parsed.OriginalSerial} — {parsed.CapacityAh:F3}Ah [{recordResult}]",
                        Logger.LogLevel.Success);
                    processedFiles.Add(filePath);
                    progress.Processed++;
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    if (msg.Contains("duplicate") || msg.Contains("unique") || msg.Contains("already"))
                    {
                        Logger.Log($"  ⚠️ Already synced: {Path.GetFileName(filePath)}", Logger.LogLevel.Warning);
                        progress.Skipped++;
                        processedFiles.Add(filePath);
                    }
                    else
                    {
                        progress.Errors++;
                        errors.Add($"{Path.GetFileName(filePath)}: {msg}");
                        Logger.Log($"  ❌ {Path.GetFileName(filePath)}: {msg}", Logger.LogLevel.Error);
                    }
                    progress.Processed++;
                }

                ProgressChanged?.Invoke(progress);
            }

            _db.MarkFilesProcessed(processedFiles, batch.Id);

            var summary = $"Batch {batch.BatchLabel}: {progress.Passed} passed, " +
                          $"{progress.Failed} failed, {progress.Skipped} skipped, {progress.Errors} errors";
            Logger.Log(summary, Logger.LogLevel.Success);

            _db.UpdateStatus(batch.Id, BatchStatus.Sent,
                errors.Count > 0 ? string.Join("; ", errors.Take(5)) : null);
        }
        catch (Exception ex)
        {
            Logger.Log($"Batch send failed: {ex.Message}", Logger.LogLevel.Error);
            _db.UpdateStatus(batch.Id, BatchStatus.Failed, ex.Message);
        }
    }

    private async Task PreloadCellTypes()
    {
        _cellTypeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var existingTypes = await _api.GetAsync("cell-types");
            var typeList = existingTypes?["data"] as JArray;
            if (typeList != null)
            {
                foreach (var t in typeList)
                {
                    var name = t["name"]?.ToString();
                    var id = t["id"]?.ToString();
                    if (name != null && id != null)
                        _cellTypeCache[name] = id;
                }
                Logger.Log($"  Loaded {_cellTypeCache.Count} cell types", Logger.LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"  Could not preload cell types: {ex.Message}", Logger.LogLevel.Warning);
        }
    }

    private async Task<JObject?> FindOrCreateCellAsync(ParsedTestResult parsed, string batchLabel)
    {
        var originalSerial = parsed.OriginalSerial;
        var prefix = ExtractPrefix(originalSerial);

        // Try lookup by originalSerial + batch
        try
        {
            var lookup = await _api.GetAsync(
                $"cells/lookup?originalSerial={Uri.EscapeDataString(originalSerial)}&batch={Uri.EscapeDataString(batchLabel)}");

            var cellData = lookup?["data"] as JObject;
            if (cellData != null)
            {
                Logger.Log($"  Found existing cell: {originalSerial}", Logger.LogLevel.Info);
                return cellData;
            }
        }
        catch { }

        // Find or create cell type using cache
        string? cellTypeId = null;
        if (!string.IsNullOrEmpty(prefix) && _cellTypeCache != null)
        {
            if (_cellTypeCache.TryGetValue(prefix, out var cachedId))
            {
                cellTypeId = cachedId;
            }
            else
            {
                try
                {
                    var ctResult = await _api.PostAsync("cell-types", new
                    {
                        name = prefix,
                        chemistry = "OTHER",
                        formFactor = "C26650",
                        description = "Auto-created by AceleCore Agent",
                    });

                    var ctData = ctResult?["data"] as JObject;
                    if (ctData != null)
                    {
                        cellTypeId = ctData["id"]?.ToString();
                        if (cellTypeId != null)
                        {
                            _cellTypeCache[prefix] = cellTypeId;
                            Logger.Log($"  Created cell type: {prefix}", Logger.LogLevel.Info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"  Cell type error: {ex.Message}", Logger.LogLevel.Warning);
                }
            }
        }

        // Generate ACE serial
        string? aceSerial = null;
        if (!string.IsNullOrEmpty(cellTypeId))
        {
            try
            {
                var seqResult = await _api.GetAsync(
                    $"cells/ace-sequence?cellTypeId={Uri.EscapeDataString(cellTypeId)}" +
                    $"&testDate={Uri.EscapeDataString(parsed.TestDate.ToString("O"))}");

                var seq = seqResult?["data"]?["sequence"]?.Value<int>();
                if (seq.HasValue)
                {
                    var dateStr = parsed.TestDate.ToString("yyMMdd");
                    aceSerial = $"ACE-{prefix}-{dateStr}-{seq.Value:D4}";
                    Logger.Log($"  Generated ACE serial: {aceSerial}", Logger.LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"  ACE serial error: {ex.Message}", Logger.LogLevel.Warning);
            }
        }

        // Create cell
        try
        {
            var finalSerial = aceSerial ?? $"{batchLabel}__{originalSerial}";
            var createResult = await _api.PostAsync("cells", new
            {
                cellSerial = finalSerial,
                originalSerial,
                cellLife = "SECOND_LIFE",
                cellFormFactor = "C26650",
                currentStatus = "RECEIVED",
                cellTypeId,
                batch = batchLabel,
            });

            var cellData = createResult?["data"] as JObject;
            if (cellData != null)
            {
                Logger.Log($"  Created cell: {finalSerial}", Logger.LogLevel.Info);
                return cellData;
            }

            // If duplicate, fetch by serial
            var msg = createResult?["message"]?.ToString() ?? "";
            if (msg.ToLower().Contains("duplicate") || msg.ToLower().Contains("unique"))
            {
                Logger.Log($"  Cell exists, fetching by serial: {finalSerial}", Logger.LogLevel.Info);
                var fetchResult = await _api.GetAsync(
                    $"cells/serial/{Uri.EscapeDataString(finalSerial)}");
                return fetchResult?["data"] as JObject;
            }

            Logger.Log($"  Cell creation failed: {createResult}", Logger.LogLevel.Error);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"  Cell creation exception: {ex.Message}", Logger.LogLevel.Error);
            return null;
        }
    }

    private static string ExtractPrefix(string serial)
    {
        var prefix = serial.Split('-', '/', '\\')[0].ToUpperInvariant().Trim();
        return string.IsNullOrEmpty(prefix) ? "UNKNOWN" : prefix;
    }
}