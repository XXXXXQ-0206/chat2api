namespace Chat2ApiTray.Models;

public sealed class TraySettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8022;

    public string Provider { get; set; } = "browser";

    public bool OfflineMode { get; set; }

    public string RuntimeName { get; set; } = "windows-tray-inproc";

    public bool LaunchAtStartup { get; set; }

    public bool StartServiceOnLaunch { get; set; } = true;

    public string ProjectDirectory { get; set; } = string.Empty;

    public string BrowserChannel { get; set; } = "msedge";

    public string DeepSeekUrl { get; set; } = "https://chat.deepseek.com";

    public int ContextProbeTimeoutSeconds { get; set; } = 45;

    public int ProviderResponseTimeoutSeconds { get; set; } = 90;

    public int ShortRetentionDays { get; set; } = 7;

    public int ConversationRetentionDays { get; set; } = 30;

    public int LogRetentionDays { get; set; } = 14;

    public string BaseUrl
    {
        get
        {
            var host = Host.Trim();
            var authority = host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
                ? $"[{host}]"
                : host;
            return $"http://{authority}:{Port}";
        }
    }

    public string OpenAiBaseUrl => $"{BaseUrl}/v1";
}
