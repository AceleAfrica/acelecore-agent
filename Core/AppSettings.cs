namespace AceleCoreAgent.Core;

public class AppSettings
{
    public string WatchFolder { get; set; } = @"D:\Cell Testing Data";
    public string ApiBaseUrl { get; set; } = "http://localhost:5000/api";
    public string ApiEmail { get; set; } = "";
    public string ApiPassword { get; set; } = "";
    public int BatchStabilitySeconds { get; set; } = 5;
    public int MinFilesPerBatch { get; set; } = 5;
    public int ConnectivityPollSeconds { get; set; } = 15;
    public int LogRetentionDays { get; set; } = 30;
    public DateTime StartFromDateTime { get; set; } = DateTime.Now;
    public bool AutoConfirmSend { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public static string CurrentVersion => "1.0.0";
    public static string UpdateCheckUrl => "https://raw.githubusercontent.com/yourrepo/acelecore-agent/main/version.txt";
}