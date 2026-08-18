using AceleCoreAgent.Watcher;

namespace AceleCoreAgent.UI;

public class ConfirmBatchForm : Form
{
    public ConfirmBatchForm(BatchReadyEventArgs batch)
    {
        Text = "New Batch Ready";
        Size = new Size(460, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 9);
        TopMost = true;

        var icon = new Label
        {
            Text = "📦",
            Font = new Font("Segoe UI", 32),
            Location = new Point(20, 20),
            Size = new Size(60, 60),
        };

        var title = new Label
        {
            Text = "New Batch Ready to Send",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(90, 25),
            Size = new Size(340, 28),
        };

        var details = new Label
        {
            Text = $"Batch: {batch.BatchLabel}\n" +
                   $"Folder: {Path.GetFileName(batch.FolderPath)}\n" +
                   $"Files detected: {batch.Files.Count}\n" +
                   $"Detected at: {batch.DetectedAt:HH:mm:ss}",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(90, 107, 122),
            Location = new Point(90, 65),
            Size = new Size(340, 100),
        };

        var sendBtn = new Button
        {
            Text = "✅  Send Now",
            Location = new Point(90, 180),
            Size = new Size(160, 40),
            BackColor = Color.FromArgb(46, 134, 171),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            DialogResult = DialogResult.OK,
            Cursor = Cursors.Hand,
        };
        sendBtn.FlatAppearance.BorderSize = 0;

        var skipBtn = new Button
        {
            Text = "Skip",
            Location = new Point(270, 180),
            Size = new Size(100, 40),
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel,
            Cursor = Cursors.Hand,
        };

        Controls.AddRange(new Control[] { icon, title, details, sendBtn, skipBtn });
        AcceptButton = sendBtn;
        CancelButton = skipBtn;
    }
}