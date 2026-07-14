using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Chat2ApiTray.Models;
using Chat2ApiTray.Services.Api;
using Microsoft.Playwright;

namespace Chat2ApiTray.Services;

public sealed class DeepSeekWebAdapter : IDeepSeekWebAdapter
{
    private static readonly string[] PromptSelectors = ["textarea", "[contenteditable='true']", "div[role='textbox']"];
    private static readonly string[] SendButtonSelectors =
    [
        DeepSeekComposerSelector.EnabledSendButton,
        "button[type='submit']",
        "button:has-text('发送')",
        "button:has-text('Send')",
        "[aria-label*='发送']",
        "[aria-label*='Send']"
    ];
    private static readonly string[] LoginSelectors = ["text=登录", "text=Log in", "text=Sign in"];
    private static readonly string[] FileInputSelectors = ["input[type='file']"];

    private readonly TraySettings _settings;
    private readonly FileLogger _logger;
    private readonly string _profileDirectory;
    private readonly string _sessionMarkerPath;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _contextIsHeadless;

    public DeepSeekWebAdapter(TraySettings settings, string dataDirectory, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _profileDirectory = Path.Combine(dataDirectory, "BrowserProfile");
        _sessionMarkerPath = Path.Combine(dataDirectory, "session-state.json");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_profileDirectory);
    }

    public async Task<AuthStatus> BeginLoginAsync(CancellationToken cancellationToken)
    {
        await _queue.WaitAsync(cancellationToken);
        try
        {
            var page = await EnsureLoginPageAsync(cancellationToken);
            await page.GotoAsync(_settings.DeepSeekUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            return await ReadAuthStatusAsync("Login page opened. Complete login in the browser window.", cancellationToken, checkBackgroundProfile: false);
        }
        finally
        {
            _queue.Release();
        }
    }

    public async Task<AuthStatus> AuthStatusAsync(string? message, CancellationToken cancellationToken)
    {
        await _queue.WaitAsync(cancellationToken);
        try
        {
            return await ReadAuthStatusAsync(message, cancellationToken, checkBackgroundProfile: true);
        }
        finally
        {
            _queue.Release();
        }
    }

    private async Task<AuthStatus> ReadAuthStatusAsync(string? message, CancellationToken cancellationToken, bool checkBackgroundProfile)
    {
        var page = ActivePage();
        bool? liveLoggedIn = null;
        if (page is null && checkBackgroundProfile)
        {
            try
            {
                page = await EnsureBackgroundAuthPageAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn($"DeepSeek background authentication check could not open a page: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (page is not null)
        {
            liveLoggedIn = await IsLoggedInAsync(page);
            if (liveLoggedIn == true)
            {
                await SaveSessionMarkerAsync();
            }
        }

        var savedAt = await ReadSavedAtAsync();
        var expiresAt = savedAt?.AddMinutes(360);
        var storedFresh = expiresAt is not null && expiresAt.Value > DateTimeOffset.UtcNow;
        var loggedIn = liveLoggedIn ?? storedFresh;
        var needsLogin = !loggedIn || expiresAt is not null && expiresAt.Value <= DateTimeOffset.UtcNow;
        var defaultMessage = needsLogin
            ? "DeepSeek login is missing or near expiry."
            : page is null
                ? "Stored DeepSeek session is within the configured TTL; live browser status was not checked."
                : null;

        return new AuthStatus(
            LoggedIn: loggedIn,
            NeedsLogin: needsLogin,
            LoginUrl: _settings.DeepSeekUrl,
            LastCheckedAt: DateTimeOffset.UtcNow.ToString("O"),
            LastLoginAt: savedAt?.ToString("O"),
            ExpiresAt: expiresAt?.ToString("O"),
            Message: message ?? defaultMessage);
    }

    public async Task<string> SendAsync(string prompt, string mode, bool thinking, bool webSearch, CancellationToken cancellationToken)
    {
        return await SendAsync(prompt, mode, thinking, webSearch, [], cancellationToken);
    }

    public async Task<string> SendAsync(string prompt, string mode, bool thinking, bool webSearch, IReadOnlyList<ProviderFile> files, CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        await foreach (var delta in StreamAsync(prompt, mode, thinking, webSearch, files, cancellationToken))
        {
            response.Append(delta);
        }

        return response.ToString();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        string mode,
        bool thinking,
        bool webSearch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var delta in StreamAsync(prompt, mode, thinking, webSearch, [], cancellationToken))
        {
            yield return delta;
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        string mode,
        bool thinking,
        bool webSearch,
        IReadOnlyList<ProviderFile> files,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _queue.WaitAsync(cancellationToken);
        IPage? page = null;
        try
        {
            AssistantSnapshot before;
            try
            {
                page = await CreateRequestPageAsync(cancellationToken);
                await StartNewConversationAsync(page);
                await ConfigureModeAsync(page, mode, thinking, webSearch);
                await UploadFilesAsync(page, files, cancellationToken);
                if (files.Any(file => file.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
                {
                    await CaptureAttachmentPreviewAsync(page, files.Count);
                }
                before = await ExtractAssistantSnapshotAsync(page);
                await FillPromptAsync(page, prompt);
                await SendCurrentPromptAsync(page);
            }
            catch (Exception ex) when (page is not null && !page.IsClosed)
            {
                await CapturePageDiagnosticsAsync(page, DiagnosticReason(ex));
                throw;
            }

            await foreach (var delta in ObserveResponseAsync(page, before, files.Count > 0, cancellationToken))
            {
                yield return delta;
            }

            _logger.Info("DeepSeek response observation completed.");
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && page is not null && !page.IsClosed)
            {
                await CapturePageDiagnosticsAsync(page, "request-cancelled");
            }

            if (page is not null && !page.IsClosed)
            {
                var closeTask = page.CloseAsync();
                if (await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) == closeTask)
                {
                    await closeTask.ConfigureAwait(false);
                }
                else
                {
                    _logger.Warn("DeepSeek request page close exceeded two seconds; continuing API completion.");
                }
            }

            _queue.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseContextAsync().ConfigureAwait(false);

        _playwright?.Dispose();
        _playwright = null;
    }

    private IPage? ActivePage()
    {
        if (_page is not null && !_page.IsClosed)
        {
            return _page;
        }

        return _context?.Pages.FirstOrDefault(page => !page.IsClosed);
    }

    private async Task<IPage> EnsureLoginPageAsync(CancellationToken cancellationToken)
    {
        if (_context is not null && _contextIsHeadless)
        {
            await CloseContextAsync();
        }

        if (_page is not null && !_page.IsClosed)
        {
            return _page;
        }

        if (_context is null)
        {
            _context = await LaunchContextAsync(headless: false);
        }

        _page = _context.Pages.FirstOrDefault(page => !page.IsClosed) ?? await _context.NewPageAsync();
        if (!_page.Url.StartsWith(_settings.DeepSeekUrl, StringComparison.OrdinalIgnoreCase))
        {
            await _page.GotoAsync(_settings.DeepSeekUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _page;
    }

    private async Task<IPage> CreateRequestPageAsync(CancellationToken cancellationToken)
    {
        if (_context is not null && BrowserContextModePolicy.ShouldSwitchRequestContextToHeadless(_contextIsHeadless))
        {
            var loginPage = ActivePage();
            if (loginPage is not null && !await IsLoggedInAsync(loginPage))
            {
                throw new ApiRequestException(401, "login_required", "DeepSeek login is required. Click the tray menu item '打开 DeepSeek 登录' first.");
            }

            await CloseContextAsync();
        }

        var context = _context ?? await LaunchContextAsync(headless: true);
        IPage page;
        try
        {
            page = await context.NewPageAsync();
        }
        catch (PlaywrightException)
        {
            _context = null;
            _page = null;
            context = await LaunchContextAsync(headless: true);
            page = await context.NewPageAsync();
        }

        await page.GotoAsync(_settings.DeepSeekUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (!await IsLoggedInAsync(page))
        {
            await page.CloseAsync();
            throw new ApiRequestException(401, "login_required", "DeepSeek login is required. Click the tray menu item '打开 DeepSeek 登录' first.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return page;
    }

    private async Task<IPage> EnsureBackgroundAuthPageAsync(CancellationToken cancellationToken)
    {
        if (_context is not null && !_contextIsHeadless)
        {
            return ActivePage() ?? await _context.NewPageAsync();
        }

        var context = _context ?? await LaunchContextAsync(headless: true);
        var page = ActivePage() ?? await context.NewPageAsync();
        if (!page.Url.StartsWith(_settings.DeepSeekUrl, StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(_settings.DeepSeekUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        }

        _page = page;
        cancellationToken.ThrowIfCancellationRequested();
        return page;
    }

    private async Task<IBrowserContext> LaunchContextAsync(bool headless)
    {
        _playwright ??= await Playwright.CreateAsync();
        _logger.Info($"Launching browser context in-process: channel={_settings.BrowserChannel}; headless={headless}");
        var context = await _playwright.Chromium.LaunchPersistentContextAsync(
            _profileDirectory,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Channel = string.IsNullOrWhiteSpace(_settings.BrowserChannel) ? null : _settings.BrowserChannel,
                AcceptDownloads = true,
                ViewportSize = ViewportSize.NoViewport
            });
        context.SetDefaultTimeout(10000);
        context.SetDefaultNavigationTimeout(30000);
        _context = context;
        _contextIsHeadless = headless;
        return context;
    }

    private async Task CloseContextAsync()
    {
        var context = _context;
        _context = null;
        _page = null;
        _contextIsHeadless = false;
        if (context is null)
        {
            return;
        }

        try
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to close DeepSeek browser context: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<bool> IsLoggedInAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 }).ContinueWith(_ => { });
            var prompt = await FirstVisibleAsync(page, PromptSelectors, 1500);
            if (prompt is not null)
            {
                await SaveSessionMarkerAsync();
                return true;
            }

            var login = await FirstVisibleAsync(page, LoginSelectors, 1000);
            return login is null && !page.Url.Contains("login", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task ConfigureModeAsync(IPage page, string mode, bool thinking, bool webSearch)
    {
        if (webSearch)
        {
            throw new ApiRequestException(400, "unsupported_mode_control", "Direct DeepSeek web search is disabled; declare the web_search tool instead.");
        }

        var modeControl = page.Locator(DeepSeekModeSelector.For(mode)).First;
        if (!await modeControl.IsVisibleAsync().CatchFalse())
        {
            throw new ApiRequestException(503, "mode_control_not_found", $"DeepSeek mode control for {mode} was not found.");
        }

        if (!string.Equals(await modeControl.GetAttributeAsync("aria-checked"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await modeControl.ClickAsync(new() { Force = true, Timeout = 3000 });
        }

        if (!await WaitForModeSelectionAsync(modeControl))
        {
            throw new ApiRequestException(503, "mode_switch_failed", $"DeepSeek did not activate {mode} mode.");
        }

        _logger.Info($"DeepSeek mode control applied: mode={mode}");
        var capability = ApiCatalog.Mode(mode);
        if (thinking && !capability.SupportsThinking)
        {
            throw new ApiRequestException(400, "unsupported_mode_control", $"Deep thinking is not supported by {mode} mode.");
        }

        if (capability.SupportsThinking)
        {
            await SetToggleAsync(page, "thinking", ["深度思考", "Deep Think", "Reason"], thinking);
        }

    }

    private static string[] ModeLabels(string mode)
    {
        var labels = mode switch
        {
            "expert" => new[] { "专家", "Expert", "深度思考", "reason" },
            "vision" => new[] { "识图", "图像", "图片", "Vision", "image" },
            _ => ["快速", "Fast", "普通", "chat"]
        };
        return labels;
    }

    private static async Task<bool> WaitForModeSelectionAsync(ILocator modeControl)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (string.Equals(await modeControl.GetAttributeAsync("aria-checked"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task UploadFilesAsync(IPage page, IReadOnlyList<ProviderFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        var input = await FirstVisibleAsync(page, FileInputSelectors, 2000, requireVisible: false)
            ?? throw new ApiRequestException(400, "file_upload_unavailable", "DeepSeek file upload input was not found.");
        await input.SetInputFilesAsync(files.Select(file => file.Path).ToArray());
        await WaitForAttachmentPreviewsAsync(page, files, cancellationToken);
        _logger.Info($"DeepSeek attachment preview verified: count={files.Count}");
    }

    private static async Task StartNewConversationAsync(IPage page)
    {
        await ClickAnyTextAsync(page, ["开启新对话", "新对话", "New chat", "New Chat"]);
        await Task.Delay(250);
    }

    private static async Task WaitForAttachmentPreviewsAsync(IPage page, IReadOnlyList<ProviderFile> files, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var altTexts = await page.Locator("img[src^='blob:'][alt]").EvaluateAllAsync<string[]>(
                "elements => elements.map(element => element.getAttribute('alt') || '')");
            if (files.All(file => altTexts.Any(alt => alt.EndsWith(file.Filename, StringComparison.Ordinal))))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new ApiRequestException(503, "attachment_preview_not_found", "DeepSeek did not display every uploaded attachment preview.");
    }

    private async Task SetToggleAsync(IPage page, string feature, string[] labels, bool desired)
    {
        foreach (var label in labels)
        {
            var text = page.GetByText(label, new() { Exact = false }).First;
            if (!await text.IsVisibleAsync().CatchFalse())
            {
                continue;
            }

            var locator = text.Locator("xpath=ancestor-or-self::*[@aria-pressed or @aria-checked][1]");
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            var selected = await locator.GetAttributeAsync("aria-pressed");
            var check = await locator.GetAttributeAsync("aria-checked");
            var active = selected == "true" || check == "true";
            if (active != desired)
            {
                await locator.ClickAsync(new() { Force = true, Timeout = 2500 });
                var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    selected = await locator.GetAttributeAsync("aria-pressed");
                    check = await locator.GetAttributeAsync("aria-checked");
                    active = selected == "true" || check == "true";
                    if (active == desired)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }
            }

            if (active != desired)
            {
                throw new ApiRequestException(503, "mode_control_not_applied", $"DeepSeek did not apply {feature}={desired.ToString().ToLowerInvariant()}.");
            }

            _logger.Info($"DeepSeek feature control applied: feature={feature}; enabled={desired}");
            return;
        }

        throw new ApiRequestException(503, "mode_control_not_found", $"DeepSeek control for {feature} was not found.");
    }

    private static async Task ClickAnyTextAsync(IPage page, string[] labels)
    {
        foreach (var label in labels)
        {
            var locator = page.GetByText(label, new() { Exact = false }).First;
            if (await locator.IsVisibleAsync().CatchFalse())
            {
                await locator.ClickAsync();
                return;
            }
        }
    }

    private static async Task FillPromptAsync(IPage page, string prompt)
    {
        var input = await FirstVisibleAsync(page, PromptSelectors, 5000)
            ?? throw new InvalidOperationException("DeepSeek prompt box was not found.");
        await input.ClickAsync();
        try
        {
            await input.FillAsync(string.Empty);
            await input.FillAsync(prompt);
        }
        catch
        {
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.PressAsync("Backspace");
            await page.Keyboard.InsertTextAsync(prompt);
        }
    }

    private static async Task SendCurrentPromptAsync(IPage page)
    {
        var button = await FirstVisibleAsync(page, SendButtonSelectors, 8000);
        if (button is not null)
        {
            await button.ClickAsync();
            return;
        }

        await page.Keyboard.PressAsync("Enter");
    }

    private async IAsyncEnumerable<string> ObserveResponseAsync(
        IPage page,
        AssistantSnapshot previous,
        bool hasAttachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var emitted = string.Empty;
        var lastChange = started;
        var baseline = previous;
        var lastObserved = previous;
        var refreshed = false;
        var responseStarted = false;
        var snapshotWasLost = false;
        var responseTimeout = TimeSpan.FromSeconds(Math.Clamp(_settings.ProviderResponseTimeoutSeconds, 10, 300));
        while (DateTimeOffset.UtcNow - started < responseTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ExtractAssistantSnapshotWithTimeoutAsync(page, cancellationToken);
            if (current.Count != lastObserved.Count || current.Text != lastObserved.Text)
            {
                _logger.Info($"DeepSeek assistant observation: count={current.Count}; chars={current.Text.Length}");
                lastObserved = current;
            }
            if (current.Count > baseline.Count || current.Text != baseline.Text)
            {
                responseStarted = true;
            }

            snapshotWasLost = responseStarted && string.IsNullOrWhiteSpace(current.Text);

            if (responseStarted && !string.IsNullOrWhiteSpace(current.Text))
            {
                var delta = AppendDelta(emitted, current.Text);
                if (!string.IsNullOrEmpty(delta))
                {
                    emitted += delta;
                    lastChange = DateTimeOffset.UtcNow;
                    yield return delta;
                }
            }

            if (DeepSeekComposerSelector.ShouldReloadAfterNoResponse(hasAttachments)
                && !refreshed
                && string.IsNullOrEmpty(emitted)
                && DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(15))
            {
                await ReloadWithTimeoutAsync(page, cancellationToken);
                baseline = new AssistantSnapshot(0, string.Empty);
                lastObserved = baseline;
                responseStarted = false;
                refreshed = true;
                _logger.Info("Reloaded DeepSeek conversation after no assistant DOM change.");
                await Task.Delay(250, cancellationToken);
                continue;
            }

            var settleDelay = snapshotWasLost ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(1500);
            if (!string.IsNullOrEmpty(emitted)
                && DateTimeOffset.UtcNow - lastChange >= settleDelay
                && !await IsResponseGeneratingAsync(page))
            {
                _logger.Info($"DeepSeek response settled: emitted_chars={emitted.Length}; snapshot_lost={snapshotWasLost}");
                yield break;
            }

            await Task.Delay(100, cancellationToken);
        }

        await CapturePageDiagnosticsAsync(page, "provider-timeout");
        throw new TimeoutException($"Timed out waiting for DeepSeek response after {responseTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task ReloadWithTimeoutAsync(IPage page, CancellationToken cancellationToken)
    {
        var reloadTask = page.EvaluateAsync("() => window.location.reload()");
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        if (await Task.WhenAny(reloadTask, timeoutTask).ConfigureAwait(false) != reloadTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out submitting a DeepSeek conversation reload.");
        }

        await reloadTask.ConfigureAwait(false);
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AssistantSnapshot> ExtractAssistantSnapshotWithTimeoutAsync(IPage page, CancellationToken cancellationToken)
    {
        var snapshotTask = ExtractAssistantSnapshotAsync(page);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        if (await Task.WhenAny(snapshotTask, timeoutTask).ConfigureAwait(false) != snapshotTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out reading the DeepSeek assistant DOM.");
        }

        return await snapshotTask.ConfigureAwait(false);
    }

    private async Task CapturePageDiagnosticsAsync(IPage page, string reason)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_profileDirectory) ?? _profileDirectory, "Diagnostics");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(directory);
        var stem = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{reason}";
        try
        {
            await CaptureRedactedScreenshotAsync(page, Path.Combine(directory, $"{stem}.png"));
            await File.WriteAllTextAsync(Path.Combine(directory, $"{stem}.html"), await CaptureRedactedHtmlAsync(page));
            await File.WriteAllTextAsync(Path.Combine(directory, $"{stem}.json"), JsonSerializer.Serialize(new
            {
                kind = "page-diagnostic",
                reason,
                captured_at = DateTimeOffset.UtcNow,
                redacted = true
            }));
            _logger.Warn($"Captured DeepSeek browser diagnostics: {stem}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to capture DeepSeek browser diagnostics: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task CaptureAttachmentPreviewAsync(IPage page, int attachmentCount)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_profileDirectory) ?? _profileDirectory, "Diagnostics");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(directory);
        var stem = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-attachment-preview";
        var path = Path.Combine(directory, $"{stem}.png");
        await CaptureRedactedScreenshotAsync(page, path);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{stem}.json"), JsonSerializer.Serialize(new
        {
            kind = "attachment-preview",
            attachment_count = attachmentCount,
            captured_at = DateTimeOffset.UtcNow,
            redacted = true
        }));
        _logger.Info($"Captured DeepSeek attachment preview: {path}");
    }

    private static string DiagnosticReason(Exception exception)
    {
        return exception switch
        {
            ApiRequestException apiError => $"request-{apiError.Code}",
            TimeoutException => "provider-timeout",
            PlaywrightException => "browser-dom-failure",
            OperationCanceledException => "request-cancelled",
            _ => "request-failed"
        };
    }

    private static async Task<bool> IsResponseGeneratingAsync(IPage page)
    {
        foreach (var selector in new[]
        {
            "button:has-text('停止生成')",
            "button:has-text('Stop generating')",
            "[aria-label*='停止生成']",
            "[aria-label*='Stop generating']"
        })
        {
            if (await page.Locator(selector).First.IsVisibleAsync().CatchFalse())
            {
                return true;
            }
        }

        return false;
    }

    private static async Task CaptureRedactedScreenshotAsync(IPage page, string path)
    {
        const string injectOverlay = """
            () => {
                document.getElementById('chat2api-diagnostic-redaction')?.remove();
                const overlay = document.createElement('div');
                overlay.id = 'chat2api-diagnostic-redaction';
                const root = document.documentElement;
                const body = document.body;
                const width = Math.max(root.scrollWidth, root.clientWidth, body ? body.scrollWidth : 0, body ? body.clientWidth : 0);
                const height = Math.max(root.scrollHeight, root.clientHeight, body ? body.scrollHeight : 0, body ? body.clientHeight : 0);
                Object.assign(overlay.style, {
                    position: 'absolute',
                    left: '0',
                    top: '0',
                    width: `${width}px`,
                    height: `${height}px`,
                    background: '#1f2937',
                    opacity: '1',
                    zIndex: '2147483647',
                    pointerEvents: 'none'
                });
                (body || root).appendChild(overlay);
            }
            """;
        const string removeOverlay = "() => document.getElementById('chat2api-diagnostic-redaction')?.remove()";

        await page.EvaluateAsync(injectOverlay);
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = path,
                FullPage = true
            });
        }
        finally
        {
            await page.EvaluateAsync(removeOverlay).ContinueWith(_ => { });
        }
    }

    private static async Task<string> CaptureRedactedHtmlAsync(IPage page)
    {
        const string script = """
            () => {
                const root = document.documentElement.cloneNode(true);
                root.querySelectorAll('script, style, link, meta, img, video, audio, canvas, svg, iframe, object, embed').forEach(node => node.remove());
                const textNodes = [];
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) textNodes.push(walker.currentNode);
                textNodes.forEach(node => {
                    if ((node.nodeValue || '').trim()) node.nodeValue = '[REDACTED]';
                });
                root.querySelectorAll('*').forEach(node => {
                    Array.from(node.attributes).forEach(attribute => {
                        const name = attribute.name.toLowerCase();
                        if (name !== 'class' && name !== 'role' && name !== 'type') node.removeAttribute(attribute.name);
                    });
                    if ('value' in node) node.setAttribute('value', '[REDACTED]');
                });
                return '<!doctype html>\\n' + root.outerHTML;
            }
            """;
        return await page.EvaluateAsync<string>(script);
    }

    private static string AppendDelta(string emitted, string current)
    {
        if (current.StartsWith(emitted, StringComparison.Ordinal))
        {
            return current[emitted.Length..];
        }

        if (string.IsNullOrEmpty(emitted))
        {
            return current;
        }

        var common = 0;
        var limit = Math.Min(emitted.Length, current.Length);
        while (common < limit && emitted[common] == current[common])
        {
            common += 1;
        }

        return current[common..];
    }

    private static async Task<AssistantSnapshot> ExtractAssistantSnapshotAsync(IPage page)
    {
        try
        {
            var texts = await page.EvaluateAsync<string[]>(
                "() => Array.from(document.querySelectorAll('.ds-assistant-message-main-content')).map(element => element.innerText.trim()).filter(Boolean)");
            var cleaned = texts.Select(text => text.Trim()).Where(text => text.Length > 0).ToList();
            if (cleaned.Count > 0)
            {
                return new AssistantSnapshot(cleaned.Count, cleaned[^1]);
            }
        }
        catch
        {
        }

        return new AssistantSnapshot(0, string.Empty);
    }

    private static async Task<ILocator?> FirstVisibleAsync(IPage page, string[] selectors, int timeoutMs, bool requireVisible = true)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var selector in selectors)
            {
                var locator = page.Locator(selector).First;
                var ok = requireVisible
                    ? await locator.IsVisibleAsync().CatchFalse()
                    : await locator.CountAsync().ContinueWith(task => task.Status == TaskStatus.RanToCompletion && task.Result > 0);
                if (ok)
                {
                    return locator;
                }
            }

            await Task.Delay(250);
        }

        return null;
    }

    private async Task SaveSessionMarkerAsync()
    {
        var payload = JsonSerializer.Serialize(new { savedAt = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(_sessionMarkerPath, payload);
    }

    private async Task<DateTimeOffset?> ReadSavedAtAsync()
    {
        try
        {
            if (!File.Exists(_sessionMarkerPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_sessionMarkerPath);
            var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.TryGetProperty("savedAt", out var element) && element.TryGetDateTimeOffset(out var savedAt)
                ? savedAt
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record AssistantSnapshot(int Count, string Text);
}

internal static class PlaywrightTaskExtensions
{
    public static async Task<bool> CatchFalse(this Task<bool> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<string>> CatchEmpty(this Task<IReadOnlyList<string>> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return [];
        }
    }

    public static async Task<string> CatchString(this Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }
}
