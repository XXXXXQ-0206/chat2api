using System.Text;
using Chat2ApiTray.Services.Context;

namespace Chat2ApiTray.Services.Api;

public static class ProviderCompletionPipeline
{
    public static async Task<ProviderResult> CompleteAsync(
        ProviderRequest request,
        ContextEngine contextEngine,
        Func<string, CancellationToken, Task<string>> sendPromptAsync,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        ToolRepairLoop.ValidateDeclaredToolSchemas(request.Tools);
        var prompt = await contextEngine.BuildPromptAsync(request, cancellationToken);
        var raw = await sendPromptAsync(prompt.Prompt, cancellationToken);
        return await CompleteFromRawAsync(request, prompt, raw, contextEngine, sendPromptAsync, warn, cancellationToken);
    }

    public static async Task<ProviderResult> CompleteStreamingAsync(
        ProviderRequest request,
        ContextEngine contextEngine,
        Func<string, CancellationToken, IAsyncEnumerable<string>> streamPromptAsync,
        Func<string, CancellationToken, Task<string>> sendPromptAsync,
        Func<string, Task> writeTextDeltaAsync,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        ToolRepairLoop.ValidateDeclaredToolSchemas(request.Tools);
        var prompt = await contextEngine.BuildPromptAsync(request, cancellationToken);
        var raw = new StringBuilder();
        var bufferForToolCalls = request.Tools is { Count: > 0 };

        await foreach (var delta in streamPromptAsync(prompt.Prompt, cancellationToken).WithCancellation(cancellationToken))
        {
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            raw.Append(delta);
            if (!bufferForToolCalls)
            {
                await writeTextDeltaAsync(delta);
            }
        }

        return await CompleteFromRawAsync(
            request,
            prompt,
            raw.ToString(),
            contextEngine,
            sendPromptAsync,
            warn,
            cancellationToken);
    }

    private static async Task<ProviderResult> CompleteFromRawAsync(
        ProviderRequest request,
        PromptPackage prompt,
        string raw,
        ContextEngine contextEngine,
        Func<string, CancellationToken, Task<string>> sendPromptAsync,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        var parsed = await ToolRepairLoop.CompleteWithRepairAsync(
            request,
            raw,
            repairPrompt => sendPromptAsync(repairPrompt, cancellationToken),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(parsed.ParseError))
        {
            warn($"tool envelope parse failed: {parsed.ParseError}");
            throw new ApiRequestException(
                502,
                "invalid_tool_envelope",
                "DeepSeek returned a malformed tool-call envelope after repair attempts.");
        }

        var content = parsed.Text;
        var inputTokens = prompt.Usage.FinalPromptTokens;
        var outputTokens = TokenEstimator.Estimate(content.Length > 0 ? content : raw);
        var result = new ProviderResult(
            Id: $"chat2api_{Guid.NewGuid():N}",
            Model: request.Model,
            Mode: request.Mode,
            Content: content,
            ToolCalls: parsed.ToolCalls,
            Usage: new Usage(inputTokens, outputTokens, inputTokens + outputTokens),
            Context: prompt.Usage,
            ConversationId: prompt.ConversationId);
        await contextEngine.RecordAssistantResultAsync(prompt.ConversationId, result);
        return result;
    }
}
