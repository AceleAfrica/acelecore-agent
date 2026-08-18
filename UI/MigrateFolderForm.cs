using AceleCoreAgent.Core;
using AceleCoreAgent.Queue;
using AceleCoreAgent.Sender;

namespace AceleCoreAgent.UI;

public class MigrateFolderForm : Form
{
    private readonly AppSettings _settings;
    private readonly QueueDatabase _db;
    private readonly ApiClient _api;

    private TextBox _folderBox = null!;
    private RichTextBox _logBox = null!;
    private ProgressBar _progressBar = null!;
    private Label _progressLabel = null!;
    private Button _startBtn = null!;
    private Button _browseBtn = null!;
    private Label _statsLabel = null!;
    private DateTimePicker _fromDatePicker = null!;
    private CheckBox _ignoreFromDate = null!;
    private CancellationTokenSource? _cts;
    private bool _running = false;

    public MigrateFolderForm(AppSettings settings, QueueDatabase db, ApiClient api)
    {
        _settings = settings;
        _db = db;
        _api = api;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "Manual Folder Migration — AceleCore Agent";
        Size = new Size(780, 600);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(15, 23, 42);
        Font = new Font("Segoe UI", 9);

        // Header
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(22, 33, 52),
            Padding = new Padding(16, 0, 16, 0),
        };
        var title = new Label
        {
            Text = "⬆  Manual Migration",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(16, 14),
        };
        var subtitle = new Label
        {
            Text = "Migrate historical test data from any folder — bypasses start date filter",
            Font = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(100, 130, 160),
            AutoSize = true,
            Location = new Point(16, 36),
        };
        header.Controls.AddRange(new Control[] { title, subtitle });

        // Config panel
        var configPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 110,
            BackColor = Color.FromArgb(22, 33, 52),
            Padding = new Padding(16, 8, 16, 8),
        };

        var folderLabel = new Label
        {
            Text = "Folder to migrate:",
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(16, 12),
            AutoSize = true,
        };

        _folderBox = new TextBox
        {
            Location = new Point(16, 30),
            Size = new Size(560, 26),
            BackColor = Color.FromArgb(15, 23, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9),
            PlaceholderText = @"e.g. D:\Cell Testing Data\2026\JULY\CELLS\DLIGHT",
        };

        _browseBtn = new Button
        {
            Text = "Browse",
            Location = new Point(584, 29),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 45, 70),
            ForeColor = Color.FromArgb(148, 163, 184),
            Cursor = Cursors.Hand,
        };
        _browseBtn.FlatAppearance.BorderSize = 0;
        _browseBtn.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select folder to migrate",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _folderBox.Text = dlg.SelectedPath;
        };

        _ignoreFromDate = new CheckBox
        {
            Text = "Ignore date filter — migrate ALL files regardless of date",
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(16, 64),
            AutoSize = true,
            Checked = true,
        };

        var fromLabel = new Label
        {
            Text = "Or process files after:",
            ForeColor = Color.FromArgb(100, 130, 160),
            Location = new Point(16, 88),
            AutoSize = true,
        };

        _fromDatePicker = new DateTimePicker
        {
            Location = new Point(150, 84),
            Size = new Size(200, 26),
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm",
            Value = new DateTime(2020, 1, 1),
            Enabled = false,
            BackColor = Color.FromArgb(15, 23, 42),
            ForeColor = Color.White,
        };

        _ignoreFromDate.CheckedChanged += (s, e) =>
            _fromDatePicker.Enabled = !_ignoreFromDate.Checked;

        configPanel.Controls.AddRange(new Control[]
        {
            folderLabel, _folderBox, _browseBtn,
            _ignoreFromDate, fromLabel, _fromDatePicker
        });

        // Log box
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Code", 8.5f),
            BackColor = Color.FromArgb(10, 16, 28),
            ForeColor = Color.FromArgb(148, 163, 184),
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
        };

        // Bottom bar
        var bottomBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.FromArgb(18, 27, 44),
            Padding = new Padding(12, 8, 12, 8),
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 5,
            Style = ProgressBarStyle.Continuous,
        };

        _progressLabel = new Label
        {
            Dock = DockStyle.None,
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(70, 100, 130),
            Text = "Ready",
            Location = new Point(12, 16),
            Size = new Size(400, 18),
        };

        _statsLabel = new Label
        {
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(74, 222, 128),
            Location = new Point(420, 16),
            Size = new Size(200, 18),
        };

        _startBtn = new Button
        {
            Text = "▶  Start Migration",
            Dock = DockStyle.Right,
            Width = 150,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(46, 134, 171),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _startBtn.FlatAppearance.BorderSize = 0;
        _startBtn.Click += OnStartClick;

        bottomBar.Controls.Add(_progressBar);
        bottomBar.Controls.Add(_progressLabel);
        bottomBar.Controls.Add(_statsLabel);
        bottomBar.Controls.Add(_startBtn);

        Controls.Add(_logBox);
        Controls.Add(bottomBar);
        Controls.Add(configPanel);
        Controls.Add(header);
    }

    private void AppendLog(string message, Color color)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message, color)); return; }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionColor = color;
        _logBox.AppendText(message + "\n");
        _logBox.ScrollToCaret();
    }

    private void Log(string msg, bool success = false, bool warning = false, bool error = false)
    {
        var color = error ? Color.FromArgb(248, 113, 113)
            : warning ? Color.FromArgb(251, 191, 36)
            : success ? Color.FromArgb(74, 222, 128)
            : Color.FromArgb(148, 163, 184);
        AppendLog($"[{DateTime.Now:HH:mm:ss}] {msg}", color);
    }

    private async void OnStartClick(object? sender, EventArgs e)
    {
        if (_running)
        {
            // Cancel
            _cts?.Cancel();
            _startBtn.Text = "Cancelling...";
            _startBtn.Enabled = false;
            return;
        }

        var folder = _folderBox.Text.Trim();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show("Please select a valid folder.", "Invalid Folder",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _running = true;
        _startBtn.Text = "⏹  Cancel";
        _startBtn.BackColor = Color.FromArgb(185, 28, 28);
        _browseBtn.Enabled = false;
        _logBox.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            await RunMigrationAsync(folder, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("Migration cancelled by user", warning: true);
        }
        catch (Exception ex)
        {
            Log($"Fatal error: {ex.Message}", error: true);
        }
        finally
        {
            _running = false;
            _startBtn.Text = "▶  Start Migration";
            _startBtn.BackColor = Color.FromArgb(46, 134, 171);
            _startBtn.Enabled = true;
            _browseBtn.Enabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunMigrationAsync(string rootFolder, CancellationToken ct)
    {
        var ignoreDate = _ignoreFromDate.Checked;
        var fromDate = _fromDatePicker.Value;

        Log($"Starting migration of: {rootFolder}");
        Log($"Date filter: {(ignoreDate ? "None — processing all files" : $"After {fromDate:yyyy-MM-dd HH:mm}")}");

        // Discover all batch folders (subfolders containing xlsx files)
        var batchFolders = DiscoverBatchFolders(rootFolder);
        Log($"Found {batchFolders.Count} batch folder(s)");
        Log("");

        if (batchFolders.Count == 0)
        {
            Log("No batch folders found. Make sure the folder contains subfolders with .xlsx files.", warning: true);
            return;
        }

        // Preview
        foreach (var (folder, label, count) in batchFolders)
        {
            Log($"  📁 {label} — {count} files");
        }
        Log("");

        if (!await _api.LoginAsync())
        {
            Log("Authentication failed — check API settings", error: true);
            return;
        }
        Log("Authenticated ✓", success: true);

        int totalFiles = 0, totalPassed = 0, totalFailed = 0,
            totalSkipped = 0, totalErrors = 0;

        foreach (var (folder, batchLabel, _) in batchFolders)
        {
            if (ct.IsCancellationRequested) break;

            Log($"\n── Batch: {batchLabel} ──────────────────");

            var files = Directory.GetFiles(folder, "*.xlsx", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                .Where(f => ignoreDate || File.GetLastWriteTime(f) >= fromDate)
                .ToList();

            // Skip already processed files
            var toProcess = files.Where(f => !_db.IsFileProcessed(f)).ToList();
            var alreadyDone = files.Count - toProcess.Count;

            Log($"  {files.Count} files — {toProcess.Count} to process, {alreadyDone} already done");

            if (toProcess.Count == 0)
            {
                Log("  All files already processed — skipping", warning: true);
                continue;
            }

            // Queue this batch
            var queueItem = new BatchQueueItem
            {
                FolderPath = folder,
                BatchLabel = batchLabel,
                FileCount = toProcess.Count,
                DetectedAt = DateTime.Now,
                Notes = "Manual migration",
            };
            var batchId = _db.EnqueueBatch(queueItem);
            queueItem.Id = batchId;

            // Create a sender with migration settings
            var migrationSettings = new AppSettings
            {
                WatchFolder = rootFolder,
                ApiBaseUrl = _settings.ApiBaseUrl,
                ApiEmail = _settings.ApiEmail,
                ApiPassword = _settings.ApiPassword,
                StartFromDateTime = ignoreDate ? new DateTime(2000, 1, 1) : fromDate,
            };

            var sender = new BatchSender(migrationSettings, _db, _api);
            int batchPassed = 0, batchFailed = 0, batchSkipped = 0, batchErrors = 0;

            sender.ProgressChanged += progress =>
            {
                if (InvokeRequired)
                    BeginInvoke(() => UpdateProgress(progress, toProcess.Count));
                else
                    UpdateProgress(progress, toProcess.Count);
            };

            // Hook into the log for this batch
            Logger.OnLog += OnMigrationLog;

            try
            {
                await sender.SendBatchAsync(queueItem, ct);
            }
            finally
            {
                Logger.OnLog -= OnMigrationLog;
            }

            totalFiles += toProcess.Count;
            Log($"  ✅ Batch complete", success: true);
        }

        Log("");
        Log($"═══════════════════════════════════════");
        Log($"Migration complete!", success: true);
        Log($"Total files processed: {totalFiles}");

        if (InvokeRequired)
            BeginInvoke(() => _statsLabel.Text = $"Done — {totalFiles} files");
        else
            _statsLabel.Text = $"Done — {totalFiles} files";
    }

    private void OnMigrationLog(string message, Logger.LogLevel level)
    {
        var color = level switch
        {
            Logger.LogLevel.Success => Color.FromArgb(74, 222, 128),
            Logger.LogLevel.Warning => Color.FromArgb(251, 191, 36),
            Logger.LogLevel.Error => Color.FromArgb(248, 113, 113),
            _ => Color.FromArgb(148, 163, 184),
        };
        AppendLog(message, color);
    }

    private void UpdateProgress(BatchSendProgress progress, int total)
    {
        _progressBar.Maximum = Math.Max(total, 1);
        _progressBar.Value = Math.Min(progress.Processed, total);
        _progressLabel.Text = $"{progress.Processed}/{total} — {progress.CurrentFile}";
        _statsLabel.Text = $"✅ {progress.Passed}  ❌ {progress.Failed}  ⚠️ {progress.Skipped}";
    }

    private List<(string Folder, string BatchLabel, int FileCount)> DiscoverBatchFolders(string rootFolder)
    {
        var result = new List<(string, string, int)>();

        // Check if root itself has xlsx files directly
        var rootFiles = Directory.GetFiles(rootFolder, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .ToList();

        if (rootFiles.Count > 0)
        {
            result.Add((rootFolder, BuildMigrationLabel(rootFolder, rootFolder), rootFiles.Count));
        }

        // Check all subfolders recursively
        foreach (var dir in Directory.GetDirectories(rootFolder, "*", SearchOption.AllDirectories))
        {
            var files = Directory.GetFiles(dir, "*.xlsx", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                .ToList();

            if (files.Count > 0)
            {
                var label = BuildMigrationLabel(dir, rootFolder);
                result.Add((dir, label, files.Count));
            }
        }

        return result;
    }

    private string BuildMigrationLabel(string folderPath, string rootFolder)
    {
        var relative = Path.GetRelativePath(rootFolder, folderPath);
        if (relative == ".") relative = Path.GetFileName(folderPath);

        var parts = relative.Split(Path.DirectorySeparatorChar);
        var label = string.Join("__", parts
            .Select(p => NormalizeSegment(p))
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        if (string.IsNullOrWhiteSpace(label)) label = "ROOT";
        return label.ToUpperInvariant();
    }

    private string NormalizeSegment(string segment)
    {
        var s = segment.Trim().ToUpperInvariant();
        var numMatch = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*$");
        var num = numMatch.Success ? numMatch.Groups[1].Value : null;

        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^(TEST|BATCH|RUN|SET|GROUP|ROUND|SESSION)") && num != null)
            return $"BATCH-{num}";

        return s.Replace(" ", "-").Replace(".", "").Trim('-');
    }
}