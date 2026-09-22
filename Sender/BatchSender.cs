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

    // Caches per batch send
    private Dictionary<string, string>? _sourceCache;      // name/code -> sourceId
    private Dictionary<string, string>? _sourceCodeCache;  // name -> code
    private Dictionary<string, string>? _batchCache;       // "sourceId-year-month-batchNo" -> batchId

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

        // Reset caches per batch
        _sourceCache = null;
        _sourceCodeCache = null;
        _batchCache = new Dictionary<string, string>();

        try
        {
            if (!await _api.LoginAsync())
            {
                _db.UpdateStatus(batch.Id, BatchStatus.Failed, "Authentication failed");
                return;
            }

            // Load sources from AceleCore
            await PreloadSources();

            // Parse folder structure for TC generation
            var folderInfo = FolderParser.Parse(batch.FolderPath, _settings.WatchFolder);

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

            // If folder structure matches new system, use TC-based sync
            if (folderInfo != null)
            {
                Logger.Log($"  Using TC system: {folderInfo.SourceName} | {folderInfo.Year}/{folderInfo.Month:D2} | Batch {folderInfo.BatchNo}", Logger.LogLevel.Info);

                // Find or create source
                var (sourceId, sourceCode) = await FindOrCreateSourceAsync(folderInfo.SourceName);
                if (sourceId == null)
                {
                    _db.UpdateStatus(batch.Id, BatchStatus.Failed, $"Could not find/create source: {folderInfo.SourceName}");
                    return;
                }

                // Find or create testing batch
                // Find or create testing batch
                var notes = folderInfo.ManufactureYear.HasValue
                    ? $"Manufacture year: {folderInfo.ManufactureYear} | Original label: {folderInfo.OriginalBatchLabel}"
                    : folderInfo.OriginalBatchLabel;

                var testingBatchId = await FindOrCreateTestingBatchAsync(
                    sourceId, folderInfo.Year, folderInfo.Month, folderInfo.BatchNo, notes);

                if (testingBatchId == null)
                {
                    _db.UpdateStatus(batch.Id, BatchStatus.Failed, "Could not find/create testing batch");
                    return;
                }

                // Build cell payloads for bulk sync
                var cellPayloads = new List<object>();

                foreach (var filePath in toProcess)
                {
                    if (ct.IsCancellationRequested) break;

                    var fileName = Path.GetFileName(filePath);
                    var fileInfo = FolderParser.ParseFileName(fileName);

                    if (fileInfo == null || fileInfo.RackPosition == 0)
                    {
                        Logger.Log($"  ⏭️ Skipped (bad filename): {fileName}", Logger.LogLevel.Warning);
                        progress.Skipped++;
                        progress.Processed++;
                        processedFiles.Add(filePath);
                        continue;
                    }

                    try
                    {
                        var parsed = FileParser.Parse(filePath);
                        if (parsed == null)
                        {
                            progress.Skipped++;
                            progress.Processed++;
                            processedFiles.Add(filePath);
                            continue;
                        }

                        if (parsed.IsPack)
                        {
                            progress.Skipped++;
                            progress.Processed++;
                            processedFiles.Add(filePath);
                            continue;
                        }

                        var tc = FolderParser.GenerateTC(
                            sourceCode!, parsed.TestDate, folderInfo.BatchNo, fileInfo.RackPosition);

                        var isDeadCell = parsed.CapacityAh <= 0;
                        var result = isDeadCell ? "FAIL" : ClassifyResult(
                            fileInfo.CapacityAh.HasValue && fileInfo.CapacityAh > 0
                                ? parsed.CapacityAh / fileInfo.CapacityAh * 100
                                : null);

                        var cellPayload = new Dictionary<string, object?>
                        {
                            ["temporaryCode"] = tc,
                            ["cellSerial"] = tc,
                            ["sourceId"] = sourceId,
                            ["testingBatchId"] = testingBatchId,
                            ["rackPosition"] = fileInfo.RackPosition,
                            ["cellLife"] = "SECOND_LIFE",
                            ["cellFormFactor"] = "C26650",
                            ["testRecord"] = BuildTestRecord(parsed, isDeadCell, result, fileName),
                        };

                        if (fileInfo.CapacityAh.HasValue)
                            cellPayload["nominalCapacity"] = fileInfo.CapacityAh.Value;

                        cellPayloads.Add(cellPayload);
                        processedFiles.Add(filePath);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{fileName}: {ex.Message}");
                        Logger.Log($"  ❌ Parse error {fileName}: {ex.Message}", Logger.LogLevel.Error);
                    }
                }

                // Sync in chunks of 100
                if (cellPayloads.Count > 0)
                {
                    const int chunkSize = 100;
                    for (int i = 0; i < cellPayloads.Count; i += chunkSize)
                    {
                        if (ct.IsCancellationRequested) break;

                        var chunk = cellPayloads.Skip(i).Take(chunkSize).ToList();
                        var syncResult = await _api.PostAsync("cells/agent/sync-batch", new { cells = chunk });

                        var created = syncResult?["data"]?["created"]?.Value<int>() ?? 0;
                        var skipped = syncResult?["data"]?["skipped"]?.Value<int>() ?? 0;
                        var errs = syncResult?["data"]?["errors"]?.Value<int>() ?? 0;

                        progress.Passed += created;
                        progress.Skipped += skipped;
                        progress.Errors += errs;
                        progress.Processed += chunk.Count;

                        Logger.Log($"  ✅ Chunk {i / chunkSize + 1}: {created} created, {skipped} skipped, {errs} errors",
                            Logger.LogLevel.Success);

                        ProgressChanged?.Invoke(progress);
                    }
                }
            }
            else
            {
                // ── Legacy fallback — old folder structure ────────────────────
                Logger.Log("  Using legacy sync (folder structure not recognised)", Logger.LogLevel.Warning);
                await SendBatchLegacyAsync(batch, toProcess, progress, processedFiles, errors, ct);
            }

            _db.MarkFilesProcessed(processedFiles, batch.Id);

            var summary = $"Batch {batch.BatchLabel}: {progress.Passed} created/passed, " +
                          $"{progress.Skipped} skipped, {progress.Errors} errors";
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

    // ── Source management ─────────────────────────────────────────────────────

    private async Task PreloadSources()
    {
        _sourceCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _sourceCodeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var res = await _api.GetAsync("cell-sources/active");
            var list = res?["data"] as JArray;
            if (list == null) return;

            foreach (var s in list)
            {
                var id = s["id"]?.ToString();
                var name = s["name"]?.ToString();
                var code = s["code"]?.ToString();
                if (id == null || name == null || code == null) continue;

                _sourceCache[name] = id;
                _sourceCache[code] = id;
                _sourceCodeCache[name] = code;
                _sourceCodeCache[code] = code;
            }

            Logger.Log($"  Loaded {_sourceCodeCache.Count / 2} sources", Logger.LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"  Could not preload sources: {ex.Message}", Logger.LogLevel.Warning);
        }
    }

    private async Task<(string? sourceId, string? sourceCode)> FindOrCreateSourceAsync(string folderName)
    {
        // Check cache first
        if (_sourceCache != null && _sourceCache.TryGetValue(folderName, out var cachedId))
        {
            _sourceCodeCache!.TryGetValue(folderName, out var cachedCode);
            return (cachedId, cachedCode);
        }

        // Create new source
        try
        {
            Logger.Log($"  Creating new source: {folderName}", Logger.LogLevel.Info);
            var res = await _api.PostAsync("cell-sources", new { name = folderName, category = "BR" });
            var data = res?["data"] as JObject;
            if (data == null) return (null, null);

            var id = data["id"]?.ToString();
            var code = data["code"]?.ToString();

            if (id != null && code != null)
            {
                _sourceCache![folderName] = id;
                _sourceCodeCache![folderName] = code;
                Logger.Log($"  Created source: {folderName} ({code})", Logger.LogLevel.Success);
            }

            return (id, code);
        }
        catch (Exception ex)
        {
            Logger.Log($"  Source creation failed: {ex.Message}", Logger.LogLevel.Error);
            return (null, null);
        }
    }

    private async Task<string?> FindOrCreateTestingBatchAsync(
    string sourceId, int year, int month, int batchNo, string? notes = null)
    {
        var cacheKey = $"{sourceId}-{year}-{month}-{batchNo}";
        if (_batchCache!.TryGetValue(cacheKey, out var cachedBatchId))
            return cachedBatchId;

        try
        {
            var batchDate = new DateTime(year, month, 1).ToString("yyyy-MM-dd");
            var res = await _api.PostAsync("testing-batches", new { sourceId, batchDate, batchNo, notes });

            // 409 = already exists, fetch it
            var data = res?["data"] as JObject;
            if (data == null)
            {
                // Try fetching existing
                var existing = await _api.GetAsync(
                    $"testing-batches?sourceId={sourceId}&year={year}&month={month}");
                var list = existing?["data"] as JArray;
                data = list?.FirstOrDefault(b => b["batchNo"]?.Value<int>() == batchNo) as JObject;
            }

            var id = data?["id"]?.ToString();
            if (id != null)
            {
                _batchCache[cacheKey] = id;
                Logger.Log($"  Testing batch ID: {id}", Logger.LogLevel.Info);
            }

            return id;
        }
        catch (Exception ex)
        {
            Logger.Log($"  Testing batch error: {ex.Message}", Logger.LogLevel.Error);
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildTestRecord(
        ParsedTestResult parsed, bool isDeadCell, string result, string fileName)
    {
        var tr = new Dictionary<string, object?>
        {
            ["capacityAh"] = isDeadCell ? 0.001 : parsed.CapacityAh,
            ["result"] = result,
            ["testDate"] = parsed.TestDate.ToString("O"),
            ["rawFileName"] = fileName,
        };

        if (parsed.EnergyWh.HasValue && parsed.EnergyWh > 0) tr["energyWh"] = parsed.EnergyWh.Value;
        if (parsed.DcirMohm.HasValue && parsed.DcirMohm > 0) tr["dcirMohm"] = parsed.DcirMohm.Value;
        if (parsed.OnsetVoltage.HasValue && parsed.OnsetVoltage > 0) tr["onsetVoltage"] = parsed.OnsetVoltage.Value;
        if (parsed.EndVoltage.HasValue && parsed.EndVoltage > 0) tr["endVoltage"] = parsed.EndVoltage.Value;

        return tr;
    }

    private static string ClassifyResult(double? soh)
    {
        if (soh == null) return "FAIL";
        if (soh >= 80) return "PASS";
        if (soh >= 60) return "MARGINAL";
        return "FAIL";
    }

    // ── Legacy sync for old folder structure ──────────────────────────────────

    private async Task SendBatchLegacyAsync(
        BatchQueueItem batch,
        List<string> toProcess,
        BatchSendProgress progress,
        List<string> processedFiles,
        List<string> errors,
        CancellationToken ct)
    {
        var cellTypeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Preload cell types
        try
        {
            var existingTypes = await _api.GetAsync("cell-types");
            var typeList = existingTypes?["data"] as JArray;
            if (typeList != null)
                foreach (var t in typeList)
                {
                    var name = t["name"]?.ToString();
                    var id = t["id"]?.ToString();
                    if (name != null && id != null) cellTypeCache[name] = id;
                }
        }
        catch { }

        foreach (var filePath in toProcess)
        {
            if (ct.IsCancellationRequested) break;

            progress.CurrentFile = Path.GetFileName(filePath);
            ProgressChanged?.Invoke(progress);

            try
            {
                var parsed = FileParser.Parse(filePath);
                if (parsed == null || parsed.IsPack)
                {
                    progress.Skipped++; progress.Processed++;
                    processedFiles.Add(filePath);
                    continue;
                }

                var cell = await FindOrCreateCellLegacyAsync(parsed, batch.BatchLabel, cellTypeCache);
                if (cell == null)
                {
                    progress.Errors++; progress.Processed++;
                    errors.Add($"{Path.GetFileName(filePath)}: Could not find or create cell");
                    continue;
                }

                var cellId = cell["id"]?.ToString();
                if (string.IsNullOrEmpty(cellId))
                {
                    progress.Errors++; progress.Processed++;
                    continue;
                }

                var isDeadCell = parsed.CapacityAh <= 0;
                var payload = new Dictionary<string, object?>
                {
                    ["cellId"] = cellId,
                    ["testDate"] = parsed.TestDate.ToString("O"),
                    ["capacityAh"] = isDeadCell ? 0.001 : parsed.CapacityAh,
                    ["rawFileName"] = Path.GetFileName(filePath),
                    ["performedBy"] = "acecore-agent",
                    ["sessionKey"] = batch.BatchLabel,
                    ["sessionLabel"] = batch.BatchLabel,
                    ["notes"] = $"Original: {parsed.OriginalSerial}",
                };

                if (isDeadCell) payload["result"] = "FAIL";
                if (parsed.EnergyWh.HasValue && parsed.EnergyWh > 0) payload["energyWh"] = parsed.EnergyWh.Value;
                if (parsed.DcirMohm.HasValue && parsed.DcirMohm > 0) payload["dcirMohm"] = parsed.DcirMohm.Value;
                if (parsed.OnsetVoltage.HasValue && parsed.OnsetVoltage > 0) payload["onsetVoltage"] = parsed.OnsetVoltage.Value;
                if (parsed.EndVoltage.HasValue && parsed.EndVoltage > 0) payload["endVoltage"] = parsed.EndVoltage.Value;

                var result = await _api.PostAsync("test-records", payload);
                var success = result?["success"]?.Value<bool>() ?? false;
                var message = result?["message"]?.ToString() ?? "";

                if (message.ToLower().Contains("duplicate") || message.ToLower().Contains("unique") || message.ToLower().Contains("already"))
                {
                    Logger.Log($"  ⚠️ Already synced: {Path.GetFileName(filePath)}", Logger.LogLevel.Warning);
                    progress.Skipped++;
                    processedFiles.Add(filePath);
                }
                else if (success)
                {
                    var recordResult = (result?["data"] as JObject)?["result"]?.ToString() ?? "UNKNOWN";
                    if (recordResult == "PASS") progress.Passed++;
                    else progress.Failed++;
                    Logger.Log($"  ✅ {parsed.OriginalSerial} — {parsed.CapacityAh:F3}Ah [{recordResult}]", Logger.LogLevel.Success);
                    processedFiles.Add(filePath);
                }
                else
                {
                    Logger.Log($"  ❌ {Path.GetFileName(filePath)}: {message}", Logger.LogLevel.Error);
                    errors.Add($"{Path.GetFileName(filePath)}: {message}");
                }

                progress.Processed++;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("duplicate") || msg.Contains("unique") || msg.Contains("already"))
                {
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
    }

    private async Task<JObject?> FindOrCreateCellLegacyAsync(
        ParsedTestResult parsed, string batchLabel,
        Dictionary<string, string> cellTypeCache)
    {
        var originalSerial = parsed.OriginalSerial;
        var prefix = originalSerial.Split('-', '/', '\\')[0].ToUpperInvariant().Trim();

        try
        {
            var lookup = await _api.GetAsync(
                $"cells/lookup?originalSerial={Uri.EscapeDataString(originalSerial)}&batch={Uri.EscapeDataString(batchLabel)}");
            var data = lookup?["data"] as JObject;
            if (data != null) return data;
        }
        catch { }

        string? cellTypeId = null;
        if (!string.IsNullOrEmpty(prefix))
        {
            if (!cellTypeCache.TryGetValue(prefix, out cellTypeId))
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
                    cellTypeId = ctData?["id"]?.ToString();
                    if (cellTypeId != null) cellTypeCache[prefix] = cellTypeId;
                }
                catch { }
            }
        }

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
                    aceSerial = $"ACE-{prefix}-{parsed.TestDate:yyMMdd}-{seq.Value:D4}";
            }
            catch { }
        }

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
            if (cellData != null) return cellData;

            var msg = createResult?["message"]?.ToString() ?? "";
            if (msg.ToLower().Contains("duplicate") || msg.ToLower().Contains("unique"))
            {
                var fetchResult = await _api.GetAsync($"cells/serial/{Uri.EscapeDataString(finalSerial)}");
                return fetchResult?["data"] as JObject;
            }

            return null;
        }
        catch { return null; }
    }
}