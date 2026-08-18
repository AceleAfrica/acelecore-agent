namespace AceleCoreAgent.UI;

public class SplashForm : Form
{
    private System.Windows.Forms.Timer _timer = new();

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(420, 260);
        BackColor = Color.FromArgb(15, 23, 42);
        TopMost = true;

        // Outer glow border
        var border = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(2),
            BackColor = Color.FromArgb(46, 134, 171),
        };

        var inner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 23, 42),
            Padding = new Padding(40),
        };

        // Logo circle
        var logoPanel = new Panel
        {
            Size = new Size(70, 70),
            Location = new Point(175, 35),
            BackColor = Color.Transparent,
        };
        logoPanel.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Try to draw actual icon
            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
                if (File.Exists(iconPath))
                {
                    using var icon = new Icon(iconPath, 64, 64);
                    g.DrawIcon(icon, new Rectangle(2, 2, 64, 64));
                    return;
                }
            }
            catch { }

            // Fallback to drawn circle
            using var brush = new SolidBrush(Color.FromArgb(46, 134, 171));
            g.FillEllipse(brush, 0, 0, 68, 68);
            using var font = new Font("Segoe UI", 22, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.White);
            var text = "AC";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, textBrush,
                (68 - size.Width) / 2,
                (68 - size.Height) / 2);
        };

        var title = new Label
        {
            Text = "AceleCore Agent",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.None,
            Size = new Size(380, 36),
            Location = new Point(20, 120),
        };

        var subtitle = new Label
        {
            Text = "Battery Operations Desktop Agent",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(100, 140, 170),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(380, 22),
            Location = new Point(20, 156),
        };

        var version = new Label
        {
            Text = "v1.0.0  ·  AceleAfrica",
            Font = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(60, 90, 110),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(380, 20),
            Location = new Point(20, 185),
        };

        // Loading bar
        var loadingBg = new Panel
        {
            Size = new Size(280, 4),
            Location = new Point(70, 220),
            BackColor = Color.FromArgb(30, 50, 70),
        };

        var loadingBar = new Panel
        {
            Size = new Size(0, 4),
            Location = new Point(0, 0),
            BackColor = Color.FromArgb(46, 134, 171),
        };
        loadingBg.Controls.Add(loadingBar);

        inner.Controls.AddRange(new Control[] { logoPanel, title, subtitle, version, loadingBg });
        border.Controls.Add(inner);
        Controls.Add(border);

        // Animate loading bar
        int progress = 0;
        _timer.Interval = 20;
        _timer.Tick += (s, e) =>
        {
            progress += 4;
            loadingBar.Width = Math.Min(progress * 280 / 100, 280);
            if (progress >= 100)
            {
                _timer.Stop();
                Close();
            }
        };
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}