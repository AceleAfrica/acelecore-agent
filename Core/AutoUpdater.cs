using System.Diagnostics;

namespace AceleCoreAgent.Core;

public static class AutoUpdater
{
    // Points to the release exe on GitHub Releases
    // Update this URL each time you publish a new release
    public static string DownloadUrl =>
        $"https://github.com/AceleAfrica/acelecore-agent/releases/latest/download/AceleCoreAgent.exe";

    public static async Task<bool> DownloadAndInstallAsync(string latestVersion)
    {
        try
        {
            Logger.Log($"Downloading v{latestVersion}...", Logger.LogLevel.Info);

            var currentExe = Application.ExecutablePath;
            var currentDir = Path.GetDirectoryName(currentExe)!;
            var backupExe = currentExe + ".bak";
            var newExe = currentExe + ".new";

            // Download new exe to a temp file
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Add("User-Agent", "AceleCoreAgent-AutoUpdater");

            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloaded = 0L;

            using (var fs = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var stream = await response.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;

                    if (totalBytes > 0)
                    {
                        var pct = (int)(downloaded * 100 / totalBytes);
                        Logger.Log($"Downloading... {pct}% ({downloaded / 1024 / 1024}MB)", Logger.LogLevel.Info);
                    }
                }
            }

            Logger.Log("Download complete — installing...", Logger.LogLevel.Success);

            // Write a small batch script that:
            // 1. Waits for this process to exit
            // 2. Replaces the exe
            // 3. Restarts the app
            var scriptPath = Path.Combine(currentDir, "_update.bat");
            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                if exist "{backupExe}" del /f /q "{backupExe}"
                move /y "{currentExe}" "{backupExe}"
                move /y "{newExe}" "{currentExe}"
                start "" "{currentExe}"
                del /f /q "%~f0"
                """;

            await File.WriteAllTextAsync(scriptPath, script);

            // Launch the script and exit
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Update failed: {ex.Message}", Logger.LogLevel.Error);
            return false;
        }
    }
}