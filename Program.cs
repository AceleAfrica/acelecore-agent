using AceleCoreAgent.UI;

namespace AceleCoreAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Set DPI awareness first before anything else
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch { }

        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Single instance check
        using var mutex = new Mutex(true, "AceleCoreAgent_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "AceleCore Agent is already running.\nCheck the system tray.",
                "Already Running",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // Show splash while main form initializes
        var splash = new SplashForm();
        splash.Show();
        Application.DoEvents(); // force splash to render immediately

        // Initialize main form
        var main = new MainForm();

        // Wait for splash animation to complete naturally
        while (splash.Visible)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }

        splash.Dispose();

        // Show and run main form
        main.Show();
        Application.Run(main);
    }
}