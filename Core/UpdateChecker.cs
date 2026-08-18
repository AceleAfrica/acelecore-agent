namespace AceleCoreAgent.Core;

public static class UpdateChecker
{
    public static async Task<(bool HasUpdate, string LatestVersion)> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var latest = (await http.GetStringAsync(AppSettings.UpdateCheckUrl)).Trim();
            var hasUpdate = string.Compare(latest, AppSettings.CurrentVersion,
                StringComparison.Ordinal) > 0;
            return (hasUpdate, latest);
        }
        catch
        {
            return (false, AppSettings.CurrentVersion);
        }
    }
}