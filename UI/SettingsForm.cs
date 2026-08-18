using AceleCoreAgent.Core;

namespace AceleCoreAgent.UI;

public class SettingsForm : Form
{
    public AppSettings Settings { get; private set; }

    private TextBox watchFolderBox = null!;
    private TextBox apiUrlBox = null!;
    private TextBox emailBox = null!;
    private TextBox passwordBox = null!;
    private NumericUpDown stabilityBox = null!;
    private NumericUpDown minFilesBox = null!;
    private DateTimePicker startDatePicker = null!;
    private CheckBox autoConfirmBox = null!;
    private CheckBox minimizeToTrayBox = null!;
    private CheckBox startWithWindowsBox = null!;


    public SettingsForm(AppSettings settings)
    {
        Settings = settings;
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        watchFolderBox.Text = Settings.WatchFolder;
        apiUrlBox.Text = Settings.ApiBaseUrl;
        emailBox.Text = Settings.ApiEmail;
        passwordBox.Text = Settings.ApiPassword;
        stabilityBox.Value = Settings.BatchStabilitySeconds;
        minFilesBox.Value = Settings.MinFilesPerBatch;
        startDatePicker.Value = Settings.StartFromDateTime;
        autoConfirmBox.Checked = Settings.AutoConfirmSend;
        minimizeToTrayBox.Checked = Settings.MinimizeToTrayOnClose;
        startWithWindowsBox.Checked = Settings.StartWithWindows;
    }

    private void SaveSettings()
    {
        Settings.WatchFolder = watchFolderBox.Text.Trim();
        Settings.ApiBaseUrl = apiUrlBox.Text.Trim();
        Settings.ApiEmail = emailBox.Text.Trim();
        Settings.ApiPassword = passwordBox.Text.Trim();
        Settings.BatchStabilitySeconds = (int)stabilityBox.Value;
        Settings.MinFilesPerBatch = (int)minFilesBox.Value;
        Settings.StartFromDateTime = startDatePicker.Value;
        Settings.AutoConfirmSend = autoConfirmBox.Checked;
        Settings.MinimizeToTrayOnClose = minimizeToTrayBox.Checked;
        Settings.StartWithWindows = startWithWindowsBox.Checked;
    }

    private void InitializeComponent()
    {
        Text = "AceleCore Agent — Settings";
        Size = new Size(500, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 9);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 12,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;

        void AddRow(string label, Control control)
        {
            panel.Controls.Add(new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(90, 107, 122),
            }, 0, row);
            control.Dock = DockStyle.Fill;
            panel.Controls.Add(control, 1, row);
            row++;
        }

        void AddSectionHeader(string title)
        {
            var lbl = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(46, 134, 171),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
            };
            panel.SetColumnSpan(lbl, 2);
            panel.Controls.Add(lbl, 0, row);
            row++;
        }

        watchFolderBox = new TextBox();
        apiUrlBox = new TextBox();
        emailBox = new TextBox();
        passwordBox = new TextBox { UseSystemPasswordChar = true };
        stabilityBox = new NumericUpDown { Minimum = 2, Maximum = 60, Value = 5 };
        minFilesBox = new NumericUpDown { Minimum = 1, Maximum = 500, Value = 5 };
        startDatePicker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm" };
        autoConfirmBox = new CheckBox { Text = "Auto-send without popup" };
        minimizeToTrayBox = new CheckBox { Text = "Minimize to tray on close" };

        AddSectionHeader("Folder Settings");
        AddRow("Watch Folder", watchFolderBox);
        AddRow("Stability (seconds)", stabilityBox);
        AddRow("Min files per batch", minFilesBox);
        AddRow("Process files after", startDatePicker);

        AddSectionHeader("API Connection");
        AddRow("API URL", apiUrlBox);
        AddRow("Email", emailBox);
        AddRow("Password", passwordBox);

        AddSectionHeader("Behavior");
        panel.Controls.Add(autoConfirmBox, 0, row); panel.SetColumnSpan(autoConfirmBox, 2); row++;
        panel.Controls.Add(minimizeToTrayBox, 0, row); panel.SetColumnSpan(minimizeToTrayBox, 2); row++;

        startWithWindowsBox = new CheckBox
        {
            Text = "Start automatically with Windows",
            ForeColor = Color.FromArgb(148, 163, 184),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(startWithWindowsBox, 0, row);
        panel.SetColumnSpan(startWithWindowsBox, 2);
        row++;

        // Buttons
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };

        var saveBtn = new Button
        {
            Text = "Save",
            Width = 90,
            Height = 32,
            BackColor = Color.FromArgb(46, 134, 171),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.Click += (s, e) => SaveSettings();

        var cancelBtn = new Button
        {
            Text = "Cancel",
            Width = 90,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel,
        };

        buttonPanel.Controls.AddRange(new Control[] { saveBtn, cancelBtn });

        Controls.Add(panel);
        Controls.Add(buttonPanel);
        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }
}