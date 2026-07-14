using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Chat2ApiTray.Models;

namespace Chat2ApiTray.Services;

public sealed class Chat2ApiServiceController : IDisposable
{
    private readonly FileLogger _logger;
    private readonly string _dataDirectory;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private EmbeddedChat2ApiServer? _server;
    private TraySettings _settings;

    public Chat2ApiServiceController(TraySettings settings, FileLogger logger, string dataDirectory)
    {
        _settings = settings;
        _logger = logger;
        _dataDirectory = dataDirectory;
    }

    public void UpdateSettings(TraySettings settings)
    {
        _settings = settings;
    }

    public bool IsProcessRunning => _server?.IsRunning == true;

    public async Task StartAsync()
    {
        if (_server?.IsRunning == true)
        {
            _logger.Info("embedded chat2api server is already running.");
            return;
        }

        _server = new EmbeddedChat2ApiServer(_settings, _dataDirectory, _logger);
        await _server.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            await _server.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to stop embedded chat2api server.");
        }
        finally
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public async Task<ServiceSnapshot> GetHealthSnapshotAsync()
    {
        try
        {
            var health = await GetJsonAsync($"{_settings.BaseUrl}/health");
            if (health is null)
            {
                return IsProcessRunning
                    ? ServiceSnapshot.Starting("等待 /health 响应。")
                    : ServiceSnapshot.Stopped();
            }

            var provider = health.RootElement.TryGetProperty("provider", out var providerElement)
                ? providerElement.GetString() ?? _settings.Provider
                : _settings.Provider;

            var runtime = health.RootElement.TryGetProperty("runtime", out var runtimeElement)
                ? runtimeElement.GetString() ?? "in-process"
                : "in-process";

            return new ServiceSnapshot(
                ProcessRunning: true,
                HealthOk: true,
                Provider: provider,
                LoggedIn: null,
                NeedsLogin: null,
                ExpiresAt: null,
                Detail: $"provider={provider}; runtime={runtime}; 服务健康");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read embedded chat2api health.");
            return IsProcessRunning
                ? ServiceSnapshot.Starting(ex.Message)
                : ServiceSnapshot.Stopped(ex.Message);
        }
    }

    public async Task<ServiceSnapshot> GetAuthSnapshotAsync()
    {
        try
        {
            var health = await GetJsonAsync($"{_settings.BaseUrl}/health");
            if (health is null)
            {
                return IsProcessRunning
                    ? ServiceSnapshot.Starting("等待 /health 响应。")
                    : ServiceSnapshot.Stopped();
            }

            var provider = health.RootElement.TryGetProperty("provider", out var providerElement)
                ? providerElement.GetString() ?? _settings.Provider
                : _settings.Provider;

            var auth = await GetJsonAsync($"{_settings.BaseUrl}/auth/status");
            if (auth is null)
            {
                return new ServiceSnapshot(
                    ProcessRunning: true,
                    HealthOk: true,
                    Provider: provider,
                    LoggedIn: null,
                    NeedsLogin: null,
                    ExpiresAt: null,
                    Detail: $"provider={provider}; auth/status 未响应");
            }

            var loggedIn = TryGetBool(auth.RootElement, "loggedIn");
            var needsLogin = TryGetBool(auth.RootElement, "needsLogin");
            var expiresAt = TryGetString(auth.RootElement, "expiresAt");
            var detail = loggedIn == true
                ? $"provider={provider}; 登录态有效"
                : $"provider={provider}; 需要登录";

            if (!string.IsNullOrWhiteSpace(expiresAt))
            {
                detail += $"; 过期={expiresAt}";
            }

            return new ServiceSnapshot(
                ProcessRunning: true,
                HealthOk: true,
                Provider: provider,
                LoggedIn: loggedIn,
                NeedsLogin: needsLogin,
                ExpiresAt: expiresAt,
                Detail: detail);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read embedded chat2api auth status.");
            return IsProcessRunning
                ? ServiceSnapshot.Starting(ex.Message)
                : ServiceSnapshot.Stopped(ex.Message);
        }
    }

    public async Task OpenLoginAsync()
    {
        await StartAsync();
        if (_server is null)
        {
            throw new InvalidOperationException("Embedded chat2api server did not start.");
        }

        await _server.BeginLoginAsync(CancellationToken.None);
    }

    public void OpenHealthUrl() => OpenUrl($"{_settings.BaseUrl}/health");

    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public void OpenProjectDirectory()
    {
        var directory = Directory.Exists(_settings.ProjectDirectory)
            ? _settings.ProjectDirectory
            : AppContext.BaseDirectory;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    public void OpenDataDirectory(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{dataDirectory}\"",
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (_server is not null)
        {
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
