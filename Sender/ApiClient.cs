using AceleCoreAgent.Core;
using Newtonsoft.Json.Linq;

namespace AceleCoreAgent.Sender;

public class ApiClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private string? _token;

    private string Base => _settings.ApiBaseUrl.TrimEnd('/');

    public ApiClient(AppSettings settings)
    {
        _settings = settings;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = false,
            MaxConnectionsPerServer = 2,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<bool> LoginAsync()
    {
        try
        {
            var url = $"{Base}/auth/login";
            Logger.Log($"Attempting login: {url}", Logger.LogLevel.Info);
            Logger.Log($"Login email: {_settings.ApiEmail}", Logger.LogLevel.Info);

            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                email = _settings.ApiEmail,
                password = _settings.ApiPassword,
            });

            using var content = new StringContent(
                payload,
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await _http.PostAsync(url, content);

            Logger.Log($"Login response status: {response.StatusCode}", Logger.LogLevel.Info);

            var responseBody = await response.Content.ReadAsStringAsync();
            Logger.Log($"Login response: {responseBody[..Math.Min(200, responseBody.Length)]}", Logger.LogLevel.Info);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Log($"Login failed: {responseBody}", Logger.LogLevel.Error);
                return false;
            }

            var json = JObject.Parse(responseBody);
            _token = json["data"]?["token"]?.ToString();

            if (_token != null)
            {
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                Logger.Log("Authenticated with AceleCore API", Logger.LogLevel.Success);
                return true;
            }

            Logger.Log("Login succeeded but no token found in response", Logger.LogLevel.Error);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"Login exception: {ex.GetType().Name}: {ex.Message}", Logger.LogLevel.Error);
            if (ex.InnerException != null)
                Logger.Log($"Inner: {ex.InnerException.Message}", Logger.LogLevel.Error);
            return false;
        }
    }

    public async Task<JObject?> GetAsync(string path)
    {
        var url = $"{Base}/{path.TrimStart('/')}";
        try
        {
            var response = await _http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await LoginAsync();
                response = await _http.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }
        catch (Exception ex)
        {
            Logger.Log($"GET {path} failed: {ex.Message}", Logger.LogLevel.Error);
            return null;
        }
    }

    public async Task<JObject?> PostAsync(string path, object payload)
    {
        var url = $"{Base}/{path.TrimStart('/')}";
        try
        {
            var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(payload);

            using var content = new StringContent(
                serialized,
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _http.PostAsync(url, content);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await LoginAsync();
                using var retryContent = new StringContent(
                    serialized,
                    System.Text.Encoding.UTF8,
                    "application/json");
                response = await _http.PostAsync(url, retryContent);
            }

            var json = await response.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }
        catch (Exception ex)
        {
            Logger.Log($"POST {path} failed: {ex.Message}", Logger.LogLevel.Error);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}