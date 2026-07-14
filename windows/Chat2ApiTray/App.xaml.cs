using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Chat2ApiTray.Models;
using Chat2ApiTray.Services;

namespace Chat2ApiTray;

public partial class App : System.Windows.Application
{
    private const string DataFolderName = "Chat2ApiTray";
    private const string SingleInstanceMutexName = @"Local\Chat2ApiTray.Singleton";
    private const string ActivateTrayMenuSignalName = @"Local\Chat2ApiTray.ActivateTrayMenu";

    private readonly string _dataDirectory;
    private readonly string _bootstrapLogPath;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSignal;
    private CancellationTokenSource? _showSignalCts;
    private Thread? _showSignalThread;
    private bool _ownsSingleInstanceMutex;
    private bool _forceShutdown;
    private SettingsService? _settingsService;
    private AutoStartService? _autoStartService;
    private FileLogger? _logger;
    private TraySettings? _settings;
    private Chat2ApiServiceController? _controller;
    private TrayIconService? _trayIconService;
    private DispatcherTimer? _statusTimer;
    private ServiceSnapshot _currentSnapshot = ServiceSnapshot.Stopped("正在初始化。");

    public App()
    {
        _dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataFolderName);
        _bootstrapLogPath = Path.Combine(_dataDirectory, "Logs", "startup.log");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        EnsureBootstrapLogDirectory();
        RegisterGlobalExceptionHooks();
        WriteBootstrapLine("INFO", "OnStartup begin.");

        try
        {
            if (!TryBecomeSingleInstance())
            {
                WriteBootstrapLine("INFO", "Detected existing instance. Sent activation signal.");
                Shutdown();
                return;
            }

            await InitializeAsync(e.Args);
        }
        catch (Exception ex)
        {
            LogCriticalStartupFailure("Application startup failed.", ex);
            ExitApplication();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_forceShutdown)
        {
            DisposeInfrastructure(stopService: false).GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }

    public void ExitApplication()
    {
        if (_forceShutdown)
        {
            return;
        }

        _forceShutdown = true;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            await DisposeInfrastructure(stopService: true);
            Shutdown();
        });
    }

    private async Task InitializeAsync(string[] args)
    {
        _settingsService = new SettingsService(_dataDirectory);
        _logger = new FileLogger(Path.Combine(_dataDirectory, "Logs"));
        _autoStartService = new AutoStartService();
        _settings = await _settingsService.LoadAsync();
        _autoStartService.Apply(_settings.LaunchAtStartup);

        _controller = new Chat2ApiServiceController(_settings, _logger, _dataDirectory);
        _trayIconService = new TrayIconService();
        _trayIconService.CommandRequested += OnTrayCommandRequested;
        _trayIconService.ToggleRequested += OnTrayToggleRequested;
        _trayIconService.Update(_currentSnapshot, _settings);

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _statusTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _statusTimer.Start();

        if (_settings.StartServiceOnLaunch)
        {
            await SafeRunAsync("启动 chat2api 服务", async () => await _controller.StartAsync());
        }

        await RefreshHealthAsync();
    }

    private void OnTrayCommandRequested(TrayCommand command)
    {
        _ = Dispatcher.BeginInvoke(async () => await HandleTrayCommandAsync(command));
    }

    private void OnTrayToggleRequested(TraySettingToggle setting, bool enabled)
    {
        _ = Dispatcher.BeginInvoke(async () => await ApplyTrayToggleAsync(setting, enabled));
    }

    private async Task HandleTrayCommandAsync(TrayCommand command)
    {
        if (_controller is null || _settings is null)
        {
            return;
        }

        switch (command)
        {
            case TrayCommand.StartService:
                await SafeRunAsync("启动服务", async () => await _controller.StartAsync());
                await RefreshHealthAsync();
                break;

            case TrayCommand.StopService:
                await SafeRunAsync("停止服务", async () => await _controller.StopAsync());
                await RefreshHealthAsync();
                break;

            case TrayCommand.RestartService:
                await SafeRunAsync("重启服务", async () => await _controller.RestartAsync());
                await RefreshHealthAsync();
                break;

            case TrayCommand.OpenLogin:
                await SafeRunAsync("打开 DeepSeek 登录", async () => await _controller.OpenLoginAsync());
                await RefreshAuthAsync();
                break;

            case TrayCommand.CheckStatus:
                await RefreshAuthAsync();
                break;

            case TrayCommand.OpenHealth:
                _controller.OpenHealthUrl();
                break;

            case TrayCommand.CopyOpenAiBaseUrl:
                System.Windows.Clipboard.SetText(_settings.OpenAiBaseUrl);
                break;

            case TrayCommand.CopyAnthropicBaseUrl:
                System.Windows.Clipboard.SetText(_settings.BaseUrl);
                break;

            case TrayCommand.OpenProjectDirectory:
                _controller.OpenProjectDirectory();
                break;

            case TrayCommand.OpenDataDirectory:
                _controller.OpenDataDirectory(_dataDirectory);
                break;

            case TrayCommand.Exit:
                ExitApplication();
                break;
        }
    }

    private async Task ApplyTrayToggleAsync(TraySettingToggle setting, bool enabled)
    {
        if (_settings is null || _settingsService is null || _autoStartService is null || _controller is null)
        {
            return;
        }

        switch (setting)
        {
            case TraySettingToggle.LaunchAtStartup:
                _settings.LaunchAtStartup = enabled;
                _autoStartService.Apply(enabled);
                break;

            case TraySettingToggle.StartServiceOnLaunch:
                _settings.StartServiceOnLaunch = enabled;
                break;
        }

        await _settingsService.SaveAsync(_settings);
        _controller.UpdateSettings(_settings);
        _trayIconService?.Update(_currentSnapshot, _settings);
        _logger?.Info($"Updated tray setting: {setting}={enabled}");
    }

    private async Task RefreshHealthAsync()
    {
        if (_controller is null || _settings is null)
        {
            return;
        }

        _currentSnapshot = await _controller.GetHealthSnapshotAsync();
        _trayIconService?.Update(_currentSnapshot, _settings);
    }

    private async Task RefreshAuthAsync()
    {
        if (_controller is null || _settings is null)
        {
            return;
        }

        _currentSnapshot = await _controller.GetAuthSnapshotAsync();
        _trayIconService?.Update(_currentSnapshot, _settings);
    }

    private async Task SafeRunAsync(string actionName, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, actionName);
        }
    }

    private bool TryBecomeSingleInstance()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateTrayMenuSignalName);
        _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName);

        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (!_ownsSingleInstanceMutex)
        {
            _showSignal.Set();
            return false;
        }

        _showSignalCts = new CancellationTokenSource();
        _showSignalThread = new Thread(() => ListenForActivationSignals(_showSignalCts.Token))
        {
            IsBackground = true,
            Name = "Chat2ApiTray.SingleInstanceListener"
        };
        _showSignalThread.Start();
        return true;
    }

    private void ListenForActivationSignals(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = _showSignal?.WaitOne();
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (received == true)
            {
                Dispatcher.BeginInvoke(() => _trayIconService?.ShowContextMenuAtCursor());
            }
        }
    }

    private async Task DisposeInfrastructure(bool stopService)
    {
        _statusTimer?.Stop();

        try
        {
            _showSignalCts?.Cancel();
            _showSignal?.Set();
        }
        catch
        {
        }

        try
        {
            if (_showSignalThread is not null && _showSignalThread.IsAlive)
            {
                _showSignalThread.Join(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
        }

        if (_trayIconService is not null)
        {
            _trayIconService.CommandRequested -= OnTrayCommandRequested;
            _trayIconService.ToggleRequested -= OnTrayToggleRequested;
        }

        if (stopService && _controller is not null)
        {
            await _controller.StopAsync();
        }

        _trayIconService?.Dispose();
        _controller?.Dispose();
        _showSignal?.Dispose();
        _showSignalCts?.Dispose();
        _showSignalThread = null;

        if (_singleInstanceMutex is not null)
        {
            if (_ownsSingleInstanceMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            _singleInstanceMutex.Dispose();
        }
    }

    private void RegisterGlobalExceptionHooks()
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            LogCriticalStartupFailure("Unhandled exception on UI thread.", eventArgs.Exception);
            eventArgs.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                LogCriticalStartupFailure("Process-level unhandled exception.", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            LogCriticalStartupFailure("Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    private void LogCriticalStartupFailure(string message, Exception exception)
    {
        var fullMessage = $"{message} {exception.GetType().Name}: {exception.Message}";
        WriteBootstrapLine("ERROR", fullMessage);
        _logger?.Error(exception, message);
    }

    private void EnsureBootstrapLogDirectory()
    {
        var directory = Path.GetDirectoryName(_bootstrapLogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            LocalDataDirectorySecurity.EnsurePrivateDirectory(directory);
        }
    }

    private void WriteBootstrapLine(string level, string message)
    {
        try
        {
            EnsureBootstrapLogDirectory();
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {LogRedactor.Redact(message)}";
            File.AppendAllText(_bootstrapLogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

}
