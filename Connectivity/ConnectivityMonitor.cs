using AceleCoreAgent.Core;

namespace AceleCoreAgent.Connectivity;

public class ConnectivityMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private System.Timers.Timer? _timer;
    private bool _isOnline = false;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public bool IsOnline => _isOnline;

    public event Action<bool>? ConnectivityChanged;

    public ConnectivityMonitor(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        _timer = new System.Timers.Timer(_settings.ConnectivityPollSeconds * 1000)
        {
            AutoReset = true
        };
        _timer.Elapsed += async (s, e) => await CheckConnectivity();
        _timer.Start();

        // Check immediately
        Task.Run(CheckConnectivity);
    }

    private async Task CheckConnectivity()
    {
        try
        {
            var url = $"{_settings.ApiBaseUrl}/health";
            Logger.Log($"Checking connectivity: {url}", Logger.LogLevel.Info);
            var response = await _http.GetAsync(url);
            Logger.Log($"Health check response: {response.StatusCode}", Logger.LogLevel.Info);

            var wasOnline = _isOnline;
            _isOnline = response.IsSuccessStatusCode;

            if (_isOnline != wasOnline)
            {
                Logger.Log(_isOnline
                    ? "Connection restored — processing queue"
                    : "Connection lost — batches will be queued",
                    _isOnline ? Logger.LogLevel.Success : Logger.LogLevel.Warning);

                ConnectivityChanged?.Invoke(_isOnline);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Health check failed: {ex.Message}", Logger.LogLevel.Warning);
            var wasOnline = _isOnline;
            _isOnline = false;
            if (wasOnline)
            {
                Logger.Log("Connection lost — batches will be queued", Logger.LogLevel.Warning);
                ConnectivityChanged?.Invoke(false);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}