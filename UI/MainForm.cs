using AceleCoreAgent.Core;
using AceleCoreAgent.Queue;
using AceleCoreAgent.Watcher;
using AceleCoreAgent.Connectivity;
using AceleCoreAgent.Sender;
using Microsoft.Win32;

namespace AceleCoreAgent.UI;

public partial class MainForm : Form
{
    private AppSettings _settings = SettingsManager.Load();
    private BatchDetector? _detector;
    private ConnectivityMonitor? _connectivity;
    private readonly QueueDatabase _db = new();
    private ApiClient? _apiClient;
    private BatchSender? _sender;
    private readonly NotifyIcon _trayIcon = new();
    private bool _isOnline = false;

    // UI Controls
    private Panel _headerPanel = null!;
    private Label _statusDot = null!;
    private Label _statusLabel = null!;
    private Label _connectivityLabel = null!;
    private ListView _queueList = null!;
    private RichTextBox _logBox = null!;
    private ProgressBar _progressBar = null!;
    private Label _progressLabel = null!;
    private Label _statsLabel = null!;
    private Button _sendBtn = null!;
    private Button _resetBtn = null!;

    public MainForm()
    {
        InitializeComponent();
        SetupTray();
        SetupLogger();

        // Start after form is shown so splash can close cleanly
        this.Shown += async (s, e) =>
        {
            Start();
            RefreshQueue();

            // Check for updates in background
            var (hasUpdate, latest) = await UpdateChecker.CheckAsync();
            if (hasUpdate)
            {
                Logger.Log($"⬆ Update available: v{latest} (current: v{AppSettings.CurrentVersion})", Logger.LogLevel.Warning);
                _trayIcon.ShowBalloonTip(5000, "Update Available",
                    $"AceleCore Agent v{latest} is available. Ask Clinton to update.", ToolTipIcon.Info);
            }
        };
    }

    private void SetupTray()
    {
        _trayIcon.Icon = LoadAppIcon();
        _trayIcon.Text = "AceleCore Agent";
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (s, e) => ShowWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (s, e) => ShowWindow());
        menu.Items.Add("Send Now", null, async (s, e) => await TriggerSendNow());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (s, e) =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        });
        _trayIcon.ContextMenuStrip = menu;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath);
        }
        catch { }
        return SystemIcons.Application;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void SetupLogger()
    {
        Logger.OnLog += (message, level) =>
        {
            if (IsDisposed) return;
            if (InvokeRequired)
                BeginInvoke(() => AppendLog(message, level));
            else
                AppendLog(message, level);
        };
    }

    private void AppendLog(string message, Logger.LogLevel level)
    {
        if (_logBox.IsDisposed) return;

        var color = level switch
        {
            Logger.LogLevel.Success => Color.FromArgb(74, 222, 128),
            Logger.LogLevel.Warning => Color.FromArgb(251, 191, 36),
            Logger.LogLevel.Error => Color.FromArgb(248, 113, 113),
            _ => Color.FromArgb(148, 163, 184),
        };

        _logBox.SuspendLayout();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(message + "\n");
        _logBox.ScrollToCaret();
        _logBox.ResumeLayout();

        if (_logBox.Lines.Length > 500)
            _logBox.Lines = _logBox.Lines.TakeLast(400).ToArray();
    }

    private void Start()
    {
        _apiClient = new ApiClient(_settings);
        _sender = new BatchSender(_settings, _db, _apiClient);
        _sender.ProgressChanged += OnProgressChanged;

        _connectivity = new ConnectivityMonitor(_settings);
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        _connectivity.Start();

        _detector = new BatchDetector(_settings);
        _detector.BatchReady += OnBatchReady;
        _detector.Start();

        Logger.Log("AceleCore Agent started", Logger.LogLevel.Info);
        Logger.Log($"Watching: {_settings.WatchFolder}", Logger.LogLevel.Info);
        Logger.Log($"Processing files after: {_settings.StartFromDateTime:yyyy-MM-dd HH:mm}", Logger.LogLevel.Info);
    }

    private void Restart()
    {
        _detector?.Dispose();
        _connectivity?.Dispose();
        _apiClient?.Dispose();

        _apiClient = new ApiClient(_settings);
        _sender = new BatchSender(_settings, _db, _apiClient);
        _sender.ProgressChanged += OnProgressChanged;

        _connectivity = new ConnectivityMonitor(_settings);
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        _connectivity.Start();

        _detector = new BatchDetector(_settings);
        _detector.BatchReady += OnBatchReady;
        _detector.Start();

        Logger.Log("Agent restarted with new settings", Logger.LogLevel.Success);
    }

    private void OnBatchReady(object? sender, BatchReadyEventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(() => OnBatchReady(sender, e)); return; }

        Logger.Log($"Batch detected: {e.BatchLabel} ({e.Files.Count} files)", Logger.LogLevel.Success);

        if (_db.IsFolderAlreadyQueued(e.FolderPath))
        {
            Logger.Log($"Batch {e.BatchLabel} already queued — skipping", Logger.LogLevel.Warning);
            return;
        }

        if (_settings.AutoConfirmSend)
        {
            EnqueueAndSend(e);
        }
        else
        {
            var confirmForm = new ConfirmBatchForm(e);
            confirmForm.TopMost = true;
            ShowWindow();
            if (confirmForm.ShowDialog() == DialogResult.OK)
                EnqueueAndSend(e);
            else
                Logger.Log($"Batch {e.BatchLabel} — skipped by user", Logger.LogLevel.Warning);
        }
    }

    private void EnqueueAndSend(BatchReadyEventArgs e)
    {
        var item = new BatchQueueItem
        {
            FolderPath = e.FolderPath,
            BatchLabel = e.BatchLabel,
            FileCount = e.Files.Count,
            DetectedAt = e.DetectedAt,
            Notes = $"{e.Files.Count} files",
        };

        var id = _db.EnqueueBatch(item);
        item.Id = id;
        RefreshQueue();

        Logger.Log($"Batch queued: {e.BatchLabel} (ID: {id})", Logger.LogLevel.Info);

        if (_isOnline)
        {
            Task.Run(async () =>
            {
                await _sender!.SendBatchAsync(item);
                if (!IsDisposed) BeginInvoke(RefreshQueue);
            });
        }
        else
        {
            Logger.Log("Offline — batch queued, will send when connection restored", Logger.LogLevel.Warning);
            _trayIcon.ShowBalloonTip(4000, "Batch Queued", $"{e.BatchLabel} — waiting for connection", ToolTipIcon.Warning);
        }
    }

    private void OnConnectivityChanged(bool online)
    {
        _isOnline = online;
        if (!IsDisposed) BeginInvoke(() => UpdateConnectivityUI(online));

        if (online)
        {
            _trayIcon.ShowBalloonTip(3000, "Connected", "Sending queued batches...", ToolTipIcon.Info);
            Task.Run(async () =>
            {
                await _sender!.ProcessQueueAsync();
                if (!IsDisposed) BeginInvoke(RefreshQueue);
            });
        }
    }

    private void UpdateConnectivityUI(bool online)
    {
        _connectivityLabel.Text = online ? "● Online" : "● Offline";
        _connectivityLabel.ForeColor = online
            ? Color.FromArgb(74, 222, 128)
            : Color.FromArgb(248, 113, 113);

        _statusDot.ForeColor = online
            ? Color.FromArgb(74, 222, 128)
            : Color.FromArgb(248, 113, 113);

        _statusLabel.Text = online ? "Watching" : "Offline";
        _trayIcon.Text = online ? "AceleCore Agent — Online" : "AceleCore Agent — Offline";
    }

    private void OnProgressChanged(BatchSendProgress progress)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnProgressChanged(progress)); return; }

        _progressBar.Maximum = Math.Max(progress.Total, 1);
        _progressBar.Value = Math.Min(progress.Processed, progress.Total);
        _progressLabel.Text = progress.Total > 0
            ? $"Sending {progress.Processed}/{progress.Total} — {progress.CurrentFile}"
            : "Idle";
    }

    private void RefreshQueue()
    {
        var items = _db.GetAll();
        _queueList.Items.Clear();

        foreach (var item in items)
        {
            var status = item.Status switch
            {
                BatchStatus.Sent => "✅ Sent",
                BatchStatus.Failed => "❌ Failed",
                BatchStatus.Sending => "⏳ Sending",
                _ => "⏸ Pending",
            };

            var lvi = new ListViewItem(item.BatchLabel)
            {
                ForeColor = item.Status switch
                {
                    BatchStatus.Sent => Color.FromArgb(74, 222, 128),
                    BatchStatus.Failed => Color.FromArgb(248, 113, 113),
                    BatchStatus.Sending => Color.FromArgb(96, 165, 250),
                    _ => Color.FromArgb(148, 163, 184),
                }
            };
            lvi.SubItems.Add(item.FileCount.ToString());
            lvi.SubItems.Add(item.DetectedAt.ToString("MM/dd HH:mm"));
            lvi.SubItems.Add(status);
            lvi.SubItems.Add(item.SentAt?.ToString("HH:mm:ss") ?? "—");
            _queueList.Items.Add(lvi);
        }

        var pending = items.Count(i => i.Status == BatchStatus.Pending || i.Status == BatchStatus.Failed);
        var sent = items.Count(i => i.Status == BatchStatus.Sent);
        _statsLabel.Text = $"{sent} sent  ·  {pending} pending";
    }

    private async Task TriggerSendNow()
    {
        Logger.Log("Manual send triggered", Logger.LogLevel.Info);
        _sendBtn.Enabled = false;
        try
        {
            await _sender!.ProcessQueueAsync();
        }
        finally
        {
            _sendBtn.Enabled = true;
            if (!IsDisposed) BeginInvoke(RefreshQueue);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_settings.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2000, "AceleCore Agent",
                "Still running in background. Right-click tray icon to exit.", ToolTipIcon.Info);
            return;
        }

        _trayIcon.Visible = false;
        _detector?.Dispose();
        _connectivity?.Dispose();
        _apiClient?.Dispose();
        base.OnFormClosing(e);
    }

    private void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (enable)
            {
                var exePath = Application.ExecutablePath;
                key?.SetValue("AceleCoreAgent", $"\"{exePath}\"");
                Logger.Log("Auto-start enabled — agent will launch on Windows startup", Logger.LogLevel.Success);
            }
            else
            {
                key?.DeleteValue("AceleCoreAgent", false);
                Logger.Log("Auto-start disabled", Logger.LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Auto-start error: {ex.Message}", Logger.LogLevel.Error);
        }
    }

    private void InitializeComponent()
    {
        Text = "AceleCore Agent";
        Size = new Size(1000, 680);
        MinimumSize = new Size(800, 560);
        BackColor = Color.FromArgb(15, 23, 42);
        Font = new Font("Segoe UI", 9);
        Icon = LoadAppIcon();

        // ===== HEADER =====
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(22, 33, 52),
            Padding = new Padding(16, 0, 16, 0),
        };

        var logo = new Label
        {
            Text = "⚡ AceleCore Agent",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(16, 15),
        };

        _statusDot = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 14),
            ForeColor = Color.FromArgb(74, 222, 128),
            AutoSize = true,
            Location = new Point(220, 16),
        };

        _statusLabel = new Label
        {
            Text = "Starting...",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(240, 20),
        };

        _connectivityLabel = new Label
        {
            Text = "● Connecting...",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(340, 20),
        };

        var settingsBtn = new Button
        {
            Text = "⚙  Settings",
            Size = new Size(90, 30),
            Location = new Point(860, 13),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 45, 70),
            ForeColor = Color.FromArgb(148, 163, 184),
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 8.5f),
        };
        settingsBtn.FlatAppearance.BorderColor = Color.FromArgb(40, 60, 90);
        settingsBtn.Click += (s, e) =>
        {
            var sf = new SettingsForm(_settings);
            if (sf.ShowDialog() == DialogResult.OK)
            {
                _settings = sf.Settings;
                SettingsManager.Save(_settings);
                SetAutoStart(_settings.StartWithWindows);
                Restart();
            }
        };

        _headerPanel.Controls.AddRange(new Control[] {
            logo, _statusDot, _statusLabel, _connectivityLabel, settingsBtn
        });

        // ===== MAIN BODY =====
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ===== LEFT PANEL =====
        var leftPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 33, 52),
            Padding = new Padding(0),
        };

        var queueHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(18, 27, 44),
            Padding = new Padding(16, 0, 8, 0),
        };

        var queueTitle = new Label
        {
            Text = "BATCH QUEUE",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 130, 160),
            AutoSize = true,
            Location = new Point(16, 14),
        };

        _statsLabel = new Label
        {
            Text = "Loading...",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(70, 100, 130),
            AutoSize = true,
            Location = new Point(180, 14),
        };

        queueHeader.Controls.AddRange(new Control[] { queueTitle, _statsLabel });

        _queueList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(22, 33, 52),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f),
            GridLines = false,
            OwnerDraw = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _queueList.Columns.Add("Batch", 140);
        _queueList.Columns.Add("Files", 45);
        _queueList.Columns.Add("Detected", 90);
        _queueList.Columns.Add("Status", 75);
        _queueList.Columns.Add("Sent", 55);

        var actionPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 106,
            BackColor = Color.FromArgb(18, 27, 44),
            Padding = new Padding(12, 8, 12, 8),
        };

        _sendBtn = new Button
        {
            Text = "▶  Send Now",
            Dock = DockStyle.Top,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(46, 134, 171),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _sendBtn.FlatAppearance.BorderSize = 0;
        _sendBtn.Click += async (s, e) => await TriggerSendNow();

        _resetBtn = new Button
        {
            Text = "↺  Reset Failed",
            Dock = DockStyle.Bottom,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 45, 70),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8f),
            Cursor = Cursors.Hand,
        };
        _resetBtn.FlatAppearance.BorderSize = 0;
        _resetBtn.Click += (s, e) =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AceleCoreAgent", "queue.db")}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE BatchQueue SET Status='Pending', RetryCount=0 WHERE Status='Failed'";
            var count = cmd.ExecuteNonQuery();
            RefreshQueue();
            Logger.Log($"Reset {count} failed batch(es)", Logger.LogLevel.Info);
        };

        var migrateBtn = new Button
        {
            Text = "⬆  Migrate Folder",
            Dock = DockStyle.Bottom,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 45, 70),
            ForeColor = Color.FromArgb(251, 191, 36),
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        migrateBtn.FlatAppearance.BorderSize = 0;
        migrateBtn.Click += (s, e) =>
        {
            var form = new MigrateFolderForm(_settings, _db, _apiClient!);
            form.ShowDialog(this);
            RefreshQueue();
        };

        actionPanel.Controls.Add(migrateBtn);
        actionPanel.Controls.Add(_resetBtn);
        actionPanel.Controls.Add(_sendBtn);

        leftPanel.Controls.Add(_queueList);
        leftPanel.Controls.Add(actionPanel);
        leftPanel.Controls.Add(queueHeader);

        // ===== RIGHT PANEL =====
        var rightPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 23, 42),
        };

        var logHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(18, 27, 44),
            Padding = new Padding(16, 0, 16, 0),
        };

        var logTitle = new Label
        {
            Text = "ACTIVITY LOG",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 130, 160),
            AutoSize = true,
            Location = new Point(16, 14),
        };

        var clearLogBtn = new Button
        {
            Text = "Clear",
            Size = new Size(50, 24),
            Location = new Point(560, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(70, 100, 130),
            Font = new Font("Segoe UI", 7.5f),
            Cursor = Cursors.Hand,
        };
        clearLogBtn.FlatAppearance.BorderSize = 0;
        clearLogBtn.Click += (s, e) => _logBox.Clear();

        logHeader.Controls.AddRange(new Control[] { logTitle, clearLogBtn });

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Code", 8.5f),
            BackColor = Color.FromArgb(10, 16, 28),
            ForeColor = Color.FromArgb(148, 163, 184),
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            Padding = new Padding(8),
            WordWrap = false,
        };

        var progressPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Color.FromArgb(18, 27, 44),
            Padding = new Padding(12, 8, 12, 6),
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 6,
            Style = ProgressBarStyle.Continuous,
            BackColor = Color.FromArgb(30, 45, 70),
            ForeColor = Color.FromArgb(46, 134, 171),
        };

        _progressLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(70, 100, 130),
            Text = "Idle — waiting for batches",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        progressPanel.Controls.Add(_progressLabel);
        progressPanel.Controls.Add(_progressBar);

        rightPanel.Controls.Add(_logBox);
        rightPanel.Controls.Add(progressPanel);
        rightPanel.Controls.Add(logHeader);

        // Assemble
        body.Controls.Add(leftPanel, 0, 0);
        body.Controls.Add(rightPanel, 1, 0);

        var divider = new Panel
        {
            Width = 1,
            Dock = DockStyle.Left,
            BackColor = Color.FromArgb(30, 45, 70),
        };
        rightPanel.Controls.Add(divider);

        Controls.Add(body);
        Controls.Add(_headerPanel);
    }
}