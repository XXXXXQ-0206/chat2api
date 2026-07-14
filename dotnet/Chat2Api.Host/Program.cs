using Chat2ApiTray.Models;
using Chat2ApiTray.Services;
using Microsoft.Playwright;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        var options = HostOptions.Parse(args);
        LocalDataDirectorySecurity.EnsurePrivateDirectory(options.DataDirectory);
        if (options.BrowserSmoke)
        {
            return await RunBrowserSmokeAsync(options);
        }

        var settings = new TraySettings
        {
            Host = options.Host,
            Port = options.Port,
            Provider = options.Provider,
            OfflineMode = options.OfflineMode,
            RuntimeName = "dotnet-console",
            BrowserChannel = options.BrowserChannel,
            DeepSeekUrl = options.DeepSeekUrl
        };
        var logger = new FileLogger(Path.Combine(options.DataDirectory, "logs"));

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await using var server = new EmbeddedChat2ApiServer(settings, options.DataDirectory, logger);
            await server.StartAsync();
            Console.WriteLine($"chat2api listening on {settings.BaseUrl} (provider={settings.Provider})");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }

            Console.WriteLine("Stopping chat2api...");
            await server.StopAsync();
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"chat2api host failed: {exception.Message}");
        return 1;
    }
}

static async Task<int> RunBrowserSmokeAsync(HostOptions options)
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
        Channel = string.IsNullOrWhiteSpace(options.BrowserChannel) ? null : options.BrowserChannel
    });
    var page = await browser.NewPageAsync();
    await page.SetContentAsync("<!doctype html><title>chat2api browser smoke</title><main>ready</main>");
    if (!string.Equals(await page.TitleAsync(), "chat2api browser smoke", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Playwright browser smoke page title did not match.");
    }

    Console.WriteLine("Playwright browser smoke passed.");
    return 0;
}

internal sealed record HostOptions(
    string Host,
    int Port,
    string Provider,
    string BrowserChannel,
    string DeepSeekUrl,
    string DataDirectory,
    bool BrowserSmoke,
    bool OfflineMode)
{
    private const string EnvironmentPrefix = "CHAT2API_";

    public static HostOptions Parse(string[] args)
    {
        var values = ParseArguments(args);
        var host = Read(values, "host", "HOST") ?? "127.0.0.1";
        var portText = Read(values, "port", "PORT") ?? "8022";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"Invalid port '{portText}'. Expected a value from 1 to 65535.");
        }

        var provider = Read(values, "provider", "PROVIDER") ?? "browser";
        var browserChannel = Read(values, "browser-channel", "BROWSER_CHANNEL")
            ?? (OperatingSystem.IsWindows() ? "msedge" : string.Empty);
        var browserSmoke = ParseBoolean(Read(values, "browser-smoke", "BROWSER_SMOKE"), "browser-smoke");
        var offlineMode = ParseBoolean(Read(values, "offline", "OFFLINE"), "offline");
        var deepSeekUrl = Read(values, "deepseek-url", "DEEPSEEK_URL") ?? "https://chat.deepseek.com";
        if (!Uri.TryCreate(deepSeekUrl, UriKind.Absolute, out var deepSeekUri)
            || (deepSeekUri.Scheme != Uri.UriSchemeHttp && deepSeekUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid DeepSeek URL '{deepSeekUrl}'. Expected an absolute HTTP(S) URL.");
        }

        var defaultDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "chat2api");
        var dataDirectory = Path.GetFullPath(Read(values, "data-dir", "DATA_DIR") ?? defaultDataDirectory);

        return new HostOptions(host, port, provider, browserChannel, deepSeekUrl, dataDirectory, browserSmoke, offlineMode);
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{argument}'. Options must start with '--'.");
            }

            if (string.Equals(argument, "--browser-smoke", StringComparison.OrdinalIgnoreCase))
            {
                values["browser-smoke"] = "true";
                continue;
            }

            if (string.Equals(argument, "--offline", StringComparison.OrdinalIgnoreCase))
            {
                values["offline"] = "true";
                continue;
            }

            var separator = argument.IndexOf('=');
            string name;
            string value;
            if (separator >= 0)
            {
                name = argument[2..separator];
                value = argument[(separator + 1)..];
            }
            else
            {
                name = argument[2..];
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for '--{name}'.");
                }

                value = args[index];
            }

            if (name is not ("host" or "port" or "provider" or "browser-channel" or "deepseek-url" or "data-dir" or "browser-smoke" or "offline"))
            {
                throw new ArgumentException($"Unknown option '--{name}'.");
            }

            values[name] = value;
        }

        return values;
    }

    private static string? Read(IReadOnlyDictionary<string, string> values, string argumentName, string environmentName)
    {
        return values.TryGetValue(argumentName, out var argumentValue)
            ? argumentValue
            : Environment.GetEnvironmentVariable(EnvironmentPrefix + environmentName);
    }

    private static bool ParseBoolean(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid boolean '{value}' for '--{name}'.");
    }
}
