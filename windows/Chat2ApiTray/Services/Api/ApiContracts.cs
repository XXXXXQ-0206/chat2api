using System.Text.Json.Nodes;

namespace Chat2ApiTray.Services.Api;

public sealed class ApiRequestException : Exception
{
    public ApiRequestException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }

    public string Code { get; }
}

public sealed record ApiMessage(
    string Role,
    string Content,
    string? Name = null,
    string? ToolCallId = null);

public sealed record ProviderRequest(
    string Model,
    string Mode,
    List<ApiMessage> Messages,
    JsonArray? Tools,
    JsonNode? ToolChoice,
    bool Thinking,
    bool WebSearch,
    string? ConversationId,
    int? MaxTokens,
    double? Temperature,
    IReadOnlyList<string>? FileIds = null);

public sealed record ProviderFile(
    string Id,
    string Path,
    string Filename,
    string? MimeType,
    long Size);

public sealed record PromptPackage(
    string Prompt,
    ContextUsage Usage,
    string ConversationId);

public sealed record ContextUsage(
    int RawTokens,
    int SummaryTokens,
    int RetrievedTokens,
    int RecentTokens,
    int FinalPromptTokens,
    int BudgetTokens,
    int ExternalManagedTokens,
    string? Diagnostic = null,
    string SummarySource = "none");

public sealed record ProviderResult(
    string Id,
    string Model,
    string Mode,
    string Content,
    List<ToolCall>? ToolCalls,
    Usage Usage,
    ContextUsage Context,
    string? ConversationId = null);

public sealed record Usage(int InputTokens, int OutputTokens, int TotalTokens);

public sealed record ToolCall(string Id, string Type, ToolFunction Function);

public sealed record ToolFunction(string Name, string Arguments);

public sealed record ParsedToolEnvelope(string Text, List<ToolCall>? ToolCalls, string? ParseError);

public sealed record ModeCapability(
    string Mode,
    string Model,
    string Label,
    bool SupportsThinking,
    bool SupportsWebSearch,
    bool SupportsImages,
    bool SupportsFiles,
    int DefaultContextTokens);

public sealed record AuthStatus(
    bool LoggedIn,
    bool NeedsLogin,
    string LoginUrl,
    string LastCheckedAt,
    string? LastLoginAt,
    string? ExpiresAt,
    string? Message);

public static class ApiCatalog
{
    public static readonly IReadOnlyDictionary<string, ModeCapability> Modes =
        new Dictionary<string, ModeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = new(
                Mode: "fast",
                Model: "deepseek-chat2api-fast",
                Label: "Fast",
                SupportsThinking: false,
                SupportsWebSearch: true,
                SupportsImages: false,
                SupportsFiles: true,
                DefaultContextTokens: 32000),
            ["expert"] = new(
                Mode: "expert",
                Model: "deepseek-chat2api-expert",
                Label: "Expert",
                SupportsThinking: true,
                SupportsWebSearch: false,
                SupportsImages: false,
                SupportsFiles: true,
                DefaultContextTokens: 64000),
            ["vision"] = new(
                Mode: "vision",
                Model: "deepseek-chat2api-vision",
                Label: "Vision",
                SupportsThinking: true,
                SupportsWebSearch: false,
                SupportsImages: true,
                SupportsFiles: true,
                DefaultContextTokens: 32000)
        };

    public static IEnumerable<ModeCapability> PublicModes => Modes.Values.Where(mode => !string.Equals(mode.Mode, "fast", StringComparison.OrdinalIgnoreCase));

    public static ModeCapability Mode(string mode)
    {
        return Modes.TryGetValue(mode, out var capability)
            ? capability
            : Modes["fast"];
    }

    public static string ModelForMode(string mode, string requestedModel)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel) && requestedModel.StartsWith("deepseek-chat2api-", StringComparison.OrdinalIgnoreCase))
        {
            return requestedModel;
        }

        return Mode(mode).Model;
    }
}
