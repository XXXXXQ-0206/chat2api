using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chat2ApiTray.Models;
using Chat2ApiTray.Services.Api;
using Chat2ApiTray.Services.Context;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Chat2ApiTray.Services;

public sealed class EmbeddedChat2ApiServer : IAsyncDisposable
{
    private readonly TraySettings _settings;
    private readonly string _dataDirectory;
    private readonly FileLogger _logger;
    private readonly ContextEngine _contextEngine;
    private readonly ResponseContinuationStore _responseContinuations;
    private readonly Func<TraySettings, string, FileLogger, IDeepSeekWebAdapter> _webAdapterFactory;
    private WebApplication? _app;
    private IDeepSeekWebAdapter? _webAdapter;

    public EmbeddedChat2ApiServer(
        TraySettings settings,
        string dataDirectory,
        FileLogger logger,
        Func<TraySettings, string, FileLogger, IDeepSeekWebAdapter>? webAdapterFactory = null)
    {
        _settings = settings;
        _dataDirectory = dataDirectory;
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_dataDirectory);
        _logger = logger;
        _contextEngine = new ContextEngine(dataDirectory, warn: logger.Warn);
        _responseContinuations = new ResponseContinuationStore(dataDirectory);
        _webAdapterFactory = webAdapterFactory ?? ((configuredSettings, configuredDataDirectory, configuredLogger) =>
            new DeepSeekWebAdapter(configuredSettings, configuredDataDirectory, configuredLogger));
    }

    public bool IsRunning => _app is not null;

    public Task<AuthStatus> BeginLoginAsync(CancellationToken cancellationToken)
    {
        ThrowIfBrowserProviderOffline();
        return RequireWeb().BeginLoginAsync(cancellationToken);
    }

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        EnsureLoopbackBinding();
        var retention = await DataRetentionService.PruneAsync(_dataDirectory, _settings);
        var deletedFiles = retention.Values.Sum();
        if (deletedFiles > 0)
        {
            _logger.Info($"data retention pruned expired files: count={deletedFiles}");
        }

        if (!IsBrowserProviderOffline())
        {
            _webAdapter = _webAdapterFactory(_settings, _dataDirectory, _logger);
        }
        _contextEngine.SetIncrementalSummarizer(new DelegateIncrementalSummarizer(SummarizeWithModelAsync));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls(_settings.BaseUrl);
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });

        var app = builder.Build();
        MapRoutes(app);
        _app = app;
        await app.StartAsync();
        _logger.Info($"embedded chat2api server started: {_settings.BaseUrl}");
    }

    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(stopCts.Token);
            await _app.DisposeAsync();
            _logger.Info("embedded chat2api server stopped.");
        }
        finally
        {
            _app = null;
            if (_webAdapter is not null)
            {
                await _webAdapter.DisposeAsync();
                _webAdapter = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private void EnsureLoopbackBinding()
    {
        var host = _settings.Host.Trim();
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))
        {
            return;
        }

        throw new InvalidOperationException("Chat2api only supports loopback host binding. Use 127.0.0.1, localhost, or an IPv6 loopback address.");
    }

    private void MapRoutes(WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"embedded API request failed: {http.Request.Path}");
                if (!http.Response.HasStarted)
                {
                    var apiError = ToApiError(ex);
                    http.Response.StatusCode = apiError?.StatusCode
                        ?? (ex is FileNotFoundException ? StatusCodes.Status404NotFound : StatusCodes.Status500InternalServerError);
                    await http.Response.WriteAsJsonAsync(new
                    {
                        error = new
                        {
                            message = ex.Message,
                            type = ex.GetType().Name,
                            code = apiError?.Code ?? (ex is FileNotFoundException ? "not_found" : "internal_error")
                        }
                    }, http.RequestAborted);
                }
            }
        });

        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            provider = _settings.Provider,
            runtime = _settings.RuntimeName,
            time = DateTimeOffset.UtcNow
        }));

        app.MapGet("/v1/modes", () => Results.Json(new
        {
            @object = "list",
            data = ApiCatalog.PublicModes
        }));

        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = ApiCatalog.PublicModes.Select(mode => new
            {
                id = mode.Model,
                @object = "model",
                owned_by = "chat2api",
                capabilities = mode
            })
        }));

        app.MapGet("/auth/status", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            await http.Response.WriteAsJsonAsync(await RequireWeb().AuthStatusAsync(null, http.RequestAborted), http.RequestAborted);
        });
        app.MapGet("/auth/login", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            await http.Response.WriteAsJsonAsync(await RequireWeb().BeginLoginAsync(http.RequestAborted), http.RequestAborted);
        });
        app.MapPost("/auth/login", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            await http.Response.WriteAsJsonAsync(await RequireWeb().BeginLoginAsync(http.RequestAborted), http.RequestAborted);
        });
        app.MapPost("/auth/wait", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            await http.Response.WriteAsJsonAsync(await WaitForLoginAsync(http), http.RequestAborted);
        });

        app.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            var body = await ReadBodyAsync(http);
            var request = ProtocolAdapters.FromOpenAi(body);
            if (ProtocolAdapters.WantsStream(body))
            {
                await StreamOpenAiAsync(http, request);
                return;
            }

            var result = await CompleteAsync(request, http.RequestAborted);
            await http.Response.WriteAsJsonAsync(ApiResponseFactory.OpenAi(result), http.RequestAborted);
        });

        app.MapPost("/v1/messages", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            var body = await ReadBodyAsync(http);
            var request = ProtocolAdapters.FromAnthropic(body);
            if (ProtocolAdapters.WantsStream(body))
            {
                await StreamAnthropicAsync(http, request);
                return;
            }

            var result = await CompleteAsync(request, http.RequestAborted);
            await http.Response.WriteAsJsonAsync(ApiResponseFactory.Anthropic(result), http.RequestAborted);
        });

        app.MapPost("/v1/responses", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            var body = await ReadBodyAsync(http);
            var request = await ResolveResponsesContinuationAsync(body, ProtocolAdapters.FromResponses(body), http.RequestAborted);
            if (ProtocolAdapters.WantsStream(body))
            {
                await StreamResponsesAsync(http, request);
                return;
            }

            var result = await CompleteAsync(request, http.RequestAborted);
            var responseId = $"resp_{Guid.NewGuid():N}";
            await SaveResponseContinuationAsync(responseId, result, http.RequestAborted);
            await http.Response.WriteAsJsonAsync(ApiResponseFactory.Responses(result, responseId), http.RequestAborted);
        });

        app.MapGet("/admin/probe/context", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            await http.Response.WriteAsJsonAsync(await ReadContextProbesAsync(http.RequestAborted), http.RequestAborted);
        });

        app.MapPost("/admin/probe/context", async (HttpContext http) =>
        {
            ThrowIfBrowserProviderOffline();
            await http.Response.WriteAsJsonAsync(await RunContextProbeAsync(http), http.RequestAborted);
        });

        app.MapPost("/v1/files", async (HttpContext http) =>
        {
            var payload = await SaveFileAsync(http);
            await http.Response.WriteAsJsonAsync(payload, http.RequestAborted);
        });
        app.MapGet("/v1/files/{id}", async (string id) => await ReadFileMetadataAsync(id));
        app.MapGet("/v1/files/{id}/content", (string id) => ReadFileContent(id));
    }

    private async Task<AuthStatus> WaitForLoginAsync(HttpContext http)
    {
        var body = await ReadBodyAsync(http);
        var timeoutMs = body["timeout_ms"] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? parsed
            : 180000;
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(timeoutMs))
        {
            var status = await RequireWeb().AuthStatusAsync(null, http.RequestAborted);
            if (status.LoggedIn)
            {
                return status;
            }

            await Task.Delay(1500, http.RequestAborted);
        }

        throw new TimeoutException("Timed out while waiting for DeepSeek login.");
    }

    private async Task<object> ReadContextProbesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = await new ContextProbeStore(_dataDirectory).ReadAllAsync();
        var byMode = records.ToDictionary(record => record.Mode, StringComparer.OrdinalIgnoreCase);
        var limits = ApiCatalog.Modes.Values.Select(mode =>
        {
            byMode.TryGetValue(mode.Mode, out var record);
            return new ContextProbeLimitDto(
                Mode: mode.Mode,
                DefaultContextTokens: mode.DefaultContextTokens,
                ManagedContextTokens: ContextEngine.ExternalManagedContextTokens,
                AcceptedChars: record?.AcceptedChars,
                EstimatedTokens: record?.EstimatedTokens,
                EffectiveTokens: record?.EffectiveTokens,
                SafetyRatio: record?.SafetyRatio,
                Source: record?.Source ?? "default",
                MeasuredAt: record?.MeasuredAt,
                Error: record?.Error);
        }).ToArray();
        return new
        {
            limits,
            records = records.Select(ToProbeRecordDto).ToArray()
        };
    }

    private async Task<ContextProbeRecordDto> RunContextProbeAsync(HttpContext http)
    {
        var body = await ReadBodyAsync(http);
        var mode = (ReadString(body, "mode") ?? "expert").Trim().ToLowerInvariant();
        if (string.Equals(mode, "fast", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiRequestException(400, "unsupported_mode", "The fast mode is disabled; use expert or vision.");
        }

        if (!ApiCatalog.Modes.ContainsKey(mode) || !ApiCatalog.PublicModes.Any(capability => capability.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApiRequestException(400, "invalid_mode", "mode must be expert or vision.");
        }

        var capability = ApiCatalog.Mode(mode);
        var minChars = Math.Max(1, ReadInt(body, "min_chars", "minChars") ?? 1024);
        var maxChars = Math.Max(minChars, ReadInt(body, "max_chars", "maxChars") ?? capability.DefaultContextTokens * 4);
        var options = new ContextProbeOptions(
            Mode: mode,
            MinChars: minChars,
            MaxChars: maxChars,
            Thinking: ReadBool(body, "thinking", "deep_thinking", "deepThinking") ?? false,
            WebSearch: ReadBool(body, "web_search", "webSearch") ?? false,
            SafetyRatio: Math.Clamp(ReadDouble(body, "safety_ratio", "safetyRatio") ?? 0.85, 0.1, 1.0));

        var runner = new ContextProbeRunner(
            new ContextProbeStore(_dataDirectory),
            request => AttemptContextProbeAsync(request, http.RequestAborted));
        var record = await runner.RunAsync(options, http.RequestAborted);
        return ToProbeRecordDto(record);
    }

    private async Task<ContextProbeAttempt> AttemptContextProbeAsync(ContextProbeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (IsMockProvider())
            {
                return ContextProbeAttempt.Accepted(request.Prompt.Length);
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.ContextProbeTimeoutSeconds, 1, 300)));
            var output = await RequireWeb().SendAsync(request.Prompt, request.Mode, request.Thinking, request.WebSearch, attemptCts.Token);
            if (string.IsNullOrWhiteSpace(output))
            {
                return ContextProbeAttempt.Rejected("empty_response");
            }

            return LooksLikeContextLimit(output)
                ? ContextProbeAttempt.Rejected("context_length_exceeded")
                : ContextProbeAttempt.Accepted(request.Prompt.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"context probe timed out for {request.Mode} after {_settings.ContextProbeTimeoutSeconds}s");
            return ContextProbeAttempt.Rejected("provider_timeout");
        }
        catch (TimeoutException)
        {
            _logger.Warn($"context probe provider timeout for {request.Mode}");
            return ContextProbeAttempt.Rejected("provider_timeout");
        }
        catch (Exception ex)
        {
            _logger.Warn($"context probe rejected for {request.Mode}: {ex.GetType().Name}: {ex.Message}");
            return ContextProbeAttempt.Rejected($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool LooksLikeContextLimit(string text)
    {
        return text.Contains("context", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("length", StringComparison.OrdinalIgnoreCase)
                || text.Contains("limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("too long", StringComparison.OrdinalIgnoreCase))
            || text.Contains("上下文", StringComparison.OrdinalIgnoreCase) && text.Contains("过长", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProviderResult> CompleteAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        request = PromoteImageFilesToVision(request);
        return await ProviderCompletionPipeline.CompleteAsync(
            request,
            _contextEngine,
            (prompt, token) => SendPromptAsync(prompt, request, token),
            warning => _logger.Warn(warning),
            cancellationToken);
    }

    private async Task StreamOpenAiAsync(HttpContext http, ProviderRequest request)
    {
        if (HasTools(request))
        {
            var bufferedResult = await CompleteStreamingAsync(request, _ => Task.CompletedTask, http.RequestAborted);
            var bufferedSession = await ApiResponseFactory.StartOpenAiStreamAsync(http.Response, request.Model);
            await WriteBufferedOpenAiTextAsync(http.Response, bufferedSession, bufferedResult);
            await ApiResponseFactory.FinishOpenAiStreamAsync(http.Response, bufferedSession, bufferedResult);
            return;
        }

        var session = await ApiResponseFactory.StartOpenAiStreamAsync(http.Response, request.Model);
        try
        {
            var result = await CompleteStreamingAsync(
                request,
                delta => ApiResponseFactory.WriteOpenAiTextDeltaAsync(http.Response, session, delta),
                http.RequestAborted);
            await ApiResponseFactory.FinishOpenAiStreamAsync(http.Response, session, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            var failure = ToStreamFailure(ex);
            _logger.Error(ex, "OpenAI stream failed after response start.");
            await ApiResponseFactory.FailOpenAiStreamAsync(http.Response, failure.Code, failure.Message);
        }
    }

    private async Task StreamAnthropicAsync(HttpContext http, ProviderRequest request)
    {
        if (HasTools(request))
        {
            var bufferedResult = await CompleteStreamingAsync(request, _ => Task.CompletedTask, http.RequestAborted);
            var bufferedSession = await ApiResponseFactory.StartAnthropicStreamAsync(http.Response, request.Model, bufferedResult.Usage.InputTokens);
            await WriteBufferedAnthropicTextAsync(http.Response, bufferedSession, bufferedResult);
            await ApiResponseFactory.FinishAnthropicStreamAsync(http.Response, bufferedSession, bufferedResult);
            return;
        }

        var session = await ApiResponseFactory.StartAnthropicStreamAsync(http.Response, request.Model, 0);
        try
        {
            var result = await CompleteStreamingAsync(
                request,
                delta => ApiResponseFactory.WriteAnthropicTextDeltaAsync(http.Response, session, delta),
                http.RequestAborted);
            await ApiResponseFactory.FinishAnthropicStreamAsync(http.Response, session, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            var failure = ToStreamFailure(ex);
            _logger.Error(ex, "Anthropic stream failed after response start.");
            await ApiResponseFactory.FailAnthropicStreamAsync(http.Response, failure.Message);
        }
    }

    private async Task StreamResponsesAsync(HttpContext http, ProviderRequest request)
    {
        if (HasTools(request))
        {
            var bufferedResult = await CompleteStreamingAsync(request, _ => Task.CompletedTask, http.RequestAborted);
            var bufferedSession = await ApiResponseFactory.StartResponsesStreamAsync(http.Response, request.Model);
            await SaveResponseContinuationAsync(bufferedSession.Id, bufferedResult, http.RequestAborted);
            await WriteBufferedResponsesTextAsync(http.Response, bufferedSession, bufferedResult);
            await ApiResponseFactory.FinishResponsesStreamAsync(http.Response, bufferedSession, bufferedResult);
            return;
        }

        var session = await ApiResponseFactory.StartResponsesStreamAsync(http.Response, request.Model);
        try
        {
            var result = await CompleteStreamingAsync(
                request,
                delta => ApiResponseFactory.WriteResponsesTextDeltaAsync(http.Response, session, delta),
                http.RequestAborted);
            await SaveResponseContinuationAsync(session.Id, result, http.RequestAborted);
            await ApiResponseFactory.FinishResponsesStreamAsync(http.Response, session, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            var failure = ToStreamFailure(ex);
            _logger.Error(ex, "Responses stream failed after response start.");
            await ApiResponseFactory.FailResponsesStreamAsync(http.Response, session, failure.Code, failure.Message);
        }
    }

    private async Task<ProviderRequest> ResolveResponsesContinuationAsync(JsonObject body, ProviderRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return request;
        }

        var previousResponseId = ReadString(body, "previous_response_id", "previousResponseId");
        if (string.IsNullOrWhiteSpace(previousResponseId))
        {
            return request;
        }

        var conversationId = await _responseContinuations.ResolveAsync(previousResponseId, cancellationToken);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ApiRequestException(404, "previous_response_not_found", "The previous response id is unknown or expired.");
        }

        return request with { ConversationId = conversationId };
    }

    private Task SaveResponseContinuationAsync(string responseId, ProviderResult result, CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(result.ConversationId)
            ? Task.CompletedTask
            : _responseContinuations.SaveAsync(responseId, result.ConversationId, cancellationToken);
    }

    private Task<ProviderResult> CompleteStreamingAsync(
        ProviderRequest request,
        Func<string, Task> writeTextDeltaAsync,
        CancellationToken cancellationToken)
    {
        request = PromoteImageFilesToVision(request);
        return ProviderCompletionPipeline.CompleteStreamingAsync(
            request,
            _contextEngine,
            (prompt, token) => StreamPromptAsync(prompt, request, token),
            (prompt, token) => SendPromptAsync(prompt, request, token),
            writeTextDeltaAsync,
            warning => _logger.Warn(warning),
            cancellationToken);
    }

    private static bool HasTools(ProviderRequest request)
    {
        return request.Tools is { Count: > 0 };
    }

    private static async Task WriteBufferedOpenAiTextAsync(HttpResponse response, ApiResponseFactory.StreamSession session, ProviderResult result)
    {
        if (string.IsNullOrEmpty(result.Content))
        {
            return;
        }

        foreach (var delta in ApiResponseFactory.SplitTextForStreaming(result.Content))
        {
            await ApiResponseFactory.WriteOpenAiTextDeltaAsync(response, session, delta);
        }
    }

    private static async Task WriteBufferedAnthropicTextAsync(HttpResponse response, ApiResponseFactory.StreamSession session, ProviderResult result)
    {
        if (string.IsNullOrEmpty(result.Content))
        {
            return;
        }

        foreach (var delta in ApiResponseFactory.SplitTextForStreaming(result.Content))
        {
            await ApiResponseFactory.WriteAnthropicTextDeltaAsync(response, session, delta);
        }
    }

    private static async Task WriteBufferedResponsesTextAsync(HttpResponse response, ApiResponseFactory.StreamSession session, ProviderResult result)
    {
        if (string.IsNullOrEmpty(result.Content))
        {
            return;
        }

        foreach (var delta in ApiResponseFactory.SplitTextForStreaming(result.Content))
        {
            await ApiResponseFactory.WriteResponsesTextDeltaAsync(response, session, delta);
        }
    }

    private Task<string> SendPromptAsync(string prompt, ProviderRequest request, CancellationToken cancellationToken)
    {
        if (IsMockProvider())
        {
            return Task.FromResult($"Mock chat2api response for {request.Mode}.\n\n{prompt}");
        }

        return RequireWeb().SendAsync(
            prompt,
            request.Mode,
            request.Thinking,
            request.WebSearch,
            ResolveProviderFiles(request),
            cancellationToken);
    }

    private IAsyncEnumerable<string> StreamPromptAsync(string prompt, ProviderRequest request, CancellationToken cancellationToken)
    {
        return IsMockProvider()
            ? MockStreamAsync($"Mock chat2api response for {request.Mode}.\n\n{prompt}", cancellationToken)
            : RequireWeb().StreamAsync(
                prompt,
                request.Mode,
                request.Thinking,
                request.WebSearch,
                ResolveProviderFiles(request),
                cancellationToken);
    }

    private async IAsyncEnumerable<string> MockStreamAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in ApiResponseFactory.SplitTextForStreaming(text, 8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    private Task<string> SummarizeWithModelAsync(IncrementalSummaryRequest request, CancellationToken cancellationToken)
    {
        if (IsMockProvider())
        {
            return Task.FromResult(string.Empty);
        }

        return RequireWeb().SendAsync(
            IncrementalSummaryPrompt.Build(request),
            request.Mode,
            thinking: false,
            webSearch: false,
            cancellationToken);
    }

    private IDeepSeekWebAdapter RequireWeb()
    {
        ThrowIfBrowserProviderOffline();
        return _webAdapter ?? throw new InvalidOperationException("Embedded server is not started.");
    }

    private bool IsBrowserProviderOffline()
    {
        return _settings.OfflineMode && !IsMockProvider();
    }

    private bool IsMockProvider()
    {
        return string.Equals(_settings.Provider.Trim(), "mock", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfBrowserProviderOffline()
    {
        if (IsBrowserProviderOffline())
        {
            throw new ApiRequestException(503, "provider_offline", "DeepSeek access is disabled by offline mode.");
        }
    }

    private IReadOnlyList<ProviderFile> ResolveProviderFiles(ProviderRequest request)
    {
        if (request.FileIds is null || request.FileIds.Count == 0)
        {
            return [];
        }

        var files = new List<ProviderFile>();
        foreach (var id in request.FileIds.Distinct(StringComparer.Ordinal))
        {
            var path = FindUploadedFile(id);
            if (path is null)
            {
                throw new ApiRequestException(404, "file_not_found", $"File {id} was not found.");
            }

            var info = new FileInfo(path);
            var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            var mimeType = provider.TryGetContentType(path, out var detectedType) ? detectedType : null;
            files.Add(new ProviderFile(
                Id: id,
                Path: path,
                Filename: FilenameFromStoredPath(id, info.Name),
                MimeType: mimeType,
                Size: info.Length));
        }

        return files;
    }

    private ProviderRequest PromoteImageFilesToVision(ProviderRequest request)
    {
        if (string.Equals(request.Mode, "vision", StringComparison.OrdinalIgnoreCase))
        {
            return request;
        }

        var files = ResolveProviderFiles(request);
        if (!files.Any(file => file.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
        {
            return request;
        }

        return request with
        {
            Mode = "vision",
            Model = ApiCatalog.Mode("vision").Model
        };
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpContext http)
    {
        return await http.Request.ReadFromJsonAsync<JsonObject>(cancellationToken: http.RequestAborted) ?? [];
    }

    private static ApiRequestException? ToApiError(Exception exception)
    {
        return exception as ApiRequestException
            ?? exception switch
            {
                JsonException or BadHttpRequestException => new ApiRequestException(400, "invalid_request", "Request body must be valid JSON."),
                TimeoutException => new ApiRequestException(504, "provider_timeout", exception.Message),
                PlaywrightException => new ApiRequestException(503, "browser_unavailable", exception.Message),
                _ => null
            };
    }

    private static (string Code, string Message) ToStreamFailure(Exception exception)
    {
        var apiError = ToApiError(exception);
        return apiError is null
            ? ("server_error", "The response stream failed.")
            : (apiError.Code, apiError.Message);
    }

    private static ContextProbeRecordDto ToProbeRecordDto(ContextProbeRecord record)
    {
        return new ContextProbeRecordDto(
            Mode: record.Mode,
            AcceptedChars: record.AcceptedChars,
            EstimatedTokens: record.EstimatedTokens,
            EffectiveTokens: record.EffectiveTokens,
            SafetyRatio: record.SafetyRatio,
            Source: record.Source,
            MeasuredAt: record.MeasuredAt,
            Error: record.Error);
    }

    private static string? ReadString(JsonObject body, params string[] names)
    {
        var node = ReadNode(body, names);
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static int? ReadInt(JsonObject body, params string[] names)
    {
        var node = ReadNode(body, names);
        return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    }

    private static double? ReadDouble(JsonObject body, params string[] names)
    {
        var node = ReadNode(body, names);
        return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    }

    private static bool? ReadBool(JsonObject body, params string[] names)
    {
        var node = ReadNode(body, names);
        return node is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean : null;
    }

    private static JsonNode? ReadNode(JsonObject body, params string[] names)
    {
        foreach (var name in names)
        {
            if (body.TryGetPropertyValue(name, out var node))
            {
                return node;
            }
        }

        return null;
    }

    private async Task<object> SaveFileAsync(HttpContext http)
    {
        var uploadDirectory = Path.Combine(_dataDirectory, "Uploads");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(uploadDirectory);

        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                throw new InvalidOperationException("No file field was found.");
            }

            var id = $"file_{Guid.NewGuid():N}";
            var safeName = Path.GetFileName(file.FileName);
            var path = Path.Combine(uploadDirectory, $"{id}_{safeName}");
            await using var stream = File.Create(path);
            await file.CopyToAsync(stream, http.RequestAborted);
            return new
            {
                id,
                @object = "file",
                filename = safeName,
                bytes = file.Length,
                path
            };
        }

        var body = await ReadBodyAsync(http);
        var sourcePath = body["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new FileNotFoundException("JSON uploads require an existing 'path'.", sourcePath);
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var dataRoot = Path.GetFullPath(_dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullSourcePath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiRequestException(400, "file_path_not_allowed", "JSON file uploads may only read files inside the chat2api data directory.");
        }

        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("JSON uploads require an existing 'path'.", sourcePath);
        }

        var jsonId = $"file_{Guid.NewGuid():N}";
        var target = Path.Combine(uploadDirectory, $"{jsonId}_{Path.GetFileName(fullSourcePath)}");
        File.Copy(fullSourcePath, target, overwrite: false);
        var info = new FileInfo(target);
        return new
        {
            id = jsonId,
            @object = "file",
            filename = info.Name,
            bytes = info.Length,
            purpose = "assistants"
        };
    }

    private Task<object> ReadFileMetadataAsync(string id)
    {
        var path = FindUploadedFile(id);
        if (path is null)
        {
            throw new FileNotFoundException($"File {id} was not found.");
        }

        var info = new FileInfo(path);
        return Task.FromResult<object>(new
        {
            id,
            @object = "file",
            filename = FilenameFromStoredPath(id, info.Name),
            bytes = info.Length,
            purpose = "assistants"
        });
    }

    private IResult ReadFileContent(string id)
    {
        var path = FindUploadedFile(id);
        if (path is null)
        {
            throw new FileNotFoundException($"File {id} was not found.");
        }

        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        var contentType = provider.TryGetContentType(path, out var detectedType)
            ? detectedType
            : "application/octet-stream";
        return Results.File(path, contentType);
    }

    private string? FindUploadedFile(string id)
    {
        if (!IsFileId(id))
        {
            return null;
        }

        var uploadDirectory = Path.Combine(_dataDirectory, "Uploads");
        return Directory.Exists(uploadDirectory)
            ? Directory.EnumerateFiles(uploadDirectory, $"{id}_*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
    }

    private static string FilenameFromStoredPath(string id, string storedName)
    {
        var prefix = id + "_";
        return storedName.StartsWith(prefix, StringComparison.Ordinal)
            ? storedName[prefix.Length..]
            : storedName;
    }

    private static bool IsFileId(string id)
    {
        return id.StartsWith("file_", StringComparison.Ordinal)
            && id.Length > 5
            && id[5..].All(char.IsAsciiLetterOrDigit);
    }

    private sealed record ContextProbeRecordDto(
        string Mode,
        int AcceptedChars,
        int EstimatedTokens,
        int EffectiveTokens,
        double SafetyRatio,
        string Source,
        DateTimeOffset MeasuredAt,
        string? Error);

    private sealed record ContextProbeLimitDto(
        string Mode,
        int DefaultContextTokens,
        int ManagedContextTokens,
        int? AcceptedChars,
        int? EstimatedTokens,
        int? EffectiveTokens,
        double? SafetyRatio,
        string Source,
        DateTimeOffset? MeasuredAt,
        string? Error);

}
