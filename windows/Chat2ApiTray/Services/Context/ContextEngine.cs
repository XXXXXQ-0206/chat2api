using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using Chat2ApiTray.Services.Api;

namespace Chat2ApiTray.Services.Context;

public sealed partial class ContextEngine
{
    public const int ExternalManagedContextTokens = 1_000_000;
    private const int MaxSummaryInputTokens = 16_000;

    private readonly ConversationStore _store;
    private readonly ContextProbeStore _probeStore;
    private readonly Action<string>? _warn;
    private IContextIndex? _contextIndex;
    private string? _contextIndexDiagnostic;
    private IIncrementalSummarizer? _summarizer;
    private readonly IConversationGate _conversationGate;

    public ContextEngine(
        string dataDirectory,
        IIncrementalSummarizer? summarizer = null,
        IContextIndex? contextIndex = null,
        Action<string>? warn = null,
        IConversationGate? conversationGate = null)
    {
        _store = new ConversationStore(dataDirectory);
        _probeStore = new ContextProbeStore(dataDirectory);
        _summarizer = summarizer;
        _contextIndex = contextIndex ?? new SqliteVectorContextIndex(Path.Combine(dataDirectory, "context-vectors.db"));
        _warn = warn;
        _conversationGate = conversationGate ?? new ConversationGate(dataDirectory);
    }

    public void SetIncrementalSummarizer(IIncrementalSummarizer summarizer)
    {
        _summarizer = summarizer;
    }

    public async Task RecordAssistantResultAsync(string conversationId, ProviderResult result)
    {
        await using var lease = await _conversationGate.EnterAsync(conversationId, CancellationToken.None);
        var record = await _store.LoadAsync(conversationId);
        var content = FormatAssistantResult(result);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var message = new ApiMessage("assistant", content);
        if (record.Messages.Count == 0 || !SameMessage(record.Messages[^1], message))
        {
            record.Messages.Add(message);
            await _store.SaveAsync(record);
        }
    }

    public async Task<PromptPackage> BuildPromptAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        var conversationId = await ResolveConversationIdAsync(request);
        await using var lease = await _conversationGate.EnterAsync(conversationId, cancellationToken);
        return await BuildPromptForConversationAsync(conversationId, request, cancellationToken);
    }

    private async Task<PromptPackage> BuildPromptForConversationAsync(string conversationId, ProviderRequest request, CancellationToken cancellationToken)
    {
        var record = await _store.LoadAsync(conversationId);
        var merged = MergeMessages(record.Messages, request.Messages);
        var rawTokens = merged.Sum(message => TokenEstimator.Estimate(message.Content));
        var managedOverflow = new List<ApiMessage>();

        while (rawTokens > ExternalManagedContextTokens && merged.Count > 1)
        {
            managedOverflow.Add(merged[0]);
            merged.RemoveAt(0);
            rawTokens = merged.Sum(message => TokenEstimator.Estimate(message.Content));
        }

        var budget = PromptBudget.ForMode(request.Mode, await ReadMeasuredContextLimitAsync(request.Mode));
        if (merged.Count > 0 && TokenEstimator.Estimate(merged[^1].Content) > budget.RecentTokens)
        {
            throw new ApiRequestException(
                400,
                "context_length_exceeded",
                $"The latest message exceeds the available context budget for mode {request.Mode}.");
        }

        var recent = TakeRecent(merged, budget.RecentTokens, out var recentTokens);
        var older = merged.Take(Math.Max(0, merged.Count - recent.Count)).ToList();
        var evicted = managedOverflow.Concat(older).ToList();
        var summarizedCount = Math.Min(record.SummarizedMessageCount, evicted.Count);
        var newlyEvicted = evicted.Skip(summarizedCount).ToList();
        var summaryResult = await BuildIncrementalSummaryAsync(request, record.Summary, newlyEvicted, budget.SummaryTokens, cancellationToken);
        var summary = summaryResult.Text;
        var summaryTokens = TokenEstimator.Estimate(summary);
        var query = request.Messages.LastOrDefault()?.Content ?? string.Empty;
        var contextIndex = _contextIndex;
        if (contextIndex is not null)
        {
            try
            {
                await contextIndex.UpsertAsync(conversationId, managedOverflow.Concat(merged).ToList(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DisableContextIndex();
                contextIndex = null;
            }
        }

        List<ApiMessage> retrieved;
        int retrievedTokens;
        if (contextIndex is null)
        {
            retrieved = RetrieveRelevant(older, query, budget.RetrievalTokens, out retrievedTokens);
        }
        else
        {
            try
            {
                retrieved = await contextIndex.SearchAsync(conversationId, query, 12, budget.RetrievalTokens, cancellationToken);
                retrievedTokens = retrieved.Sum(message => TokenEstimator.Estimate(message.Content));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DisableContextIndex();
                retrieved = RetrieveRelevant(older, query, budget.RetrievalTokens, out retrievedTokens);
            }
        }
        if (retrieved.Count == 0)
        {
            retrieved = RetrieveRelevant(older, query, budget.RetrievalTokens, out retrievedTokens);
        }

        record.Messages = merged;
        record.Summary = summary;
        record.SummarizedMessageCount = Math.Max(record.SummarizedMessageCount, evicted.Count);
        await _store.SaveAsync(record);

        var prompt = BuildPrompt(request, summary, retrieved, recent);
        var finalTokens = TokenEstimator.Estimate(prompt);
        if (finalTokens > budget.TotalTokens)
        {
            recent = TakeRecent(merged, Math.Max(1024, budget.RecentTokens - (finalTokens - budget.TotalTokens)), out recentTokens);
            prompt = BuildPrompt(request, summary, retrieved, recent);
            finalTokens = TokenEstimator.Estimate(prompt);
        }

        if (finalTokens > budget.TotalTokens)
        {
            throw new ApiRequestException(
                400,
                "context_length_exceeded",
                $"The assembled prompt exceeds the context budget for mode {request.Mode}.");
        }

        return new PromptPackage(
            Prompt: prompt,
            Usage: new ContextUsage(
                RawTokens: rawTokens,
                SummaryTokens: summaryTokens,
                RetrievedTokens: retrievedTokens,
                RecentTokens: recentTokens,
                FinalPromptTokens: finalTokens,
                BudgetTokens: budget.TotalTokens,
                ExternalManagedTokens: ExternalManagedContextTokens,
                Diagnostic: CombineDiagnostics(_contextIndexDiagnostic, summaryResult.Diagnostic),
                SummarySource: summaryResult.Source),
            ConversationId: conversationId);
    }

    private void DisableContextIndex()
    {
        _contextIndex = null;
        _contextIndexDiagnostic = "context_index_unavailable";
    }

    private static string BuildPrompt(ProviderRequest request, string summary, List<ApiMessage> retrieved, List<ApiMessage> recent)
    {
        var parts = new List<string>
        {
            BuildAgentGuard(),
            BuildToolContract(request),
            BuildControls(request)
        };

        if (!string.IsNullOrWhiteSpace(summary))
        {
            parts.Add($"Long-term rolling summary:\n{summary}");
        }

        if (retrieved.Count > 0)
        {
            parts.Add("Retrieved relevant history:\n" + string.Join("\n\n", retrieved.Select(FormatMessage)));
        }

        parts.Add("Recent conversation window:\n" + string.Join("\n\n", recent.Select(FormatMessage)));
        return string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private async Task<int?> ReadMeasuredContextLimitAsync(string mode)
    {
        var record = (await _probeStore.ReadAllAsync())
            .FirstOrDefault(candidate => string.Equals(candidate.Mode, mode, StringComparison.OrdinalIgnoreCase));
        return record is { EffectiveTokens: > 0, Error: null } ? record.EffectiveTokens : null;
    }

    private static string BuildAgentGuard()
    {
        return string.Join('\n', [
            "You are serving a local API bridge for coding agents.",
            "Follow the user's task directly and produce actionable work product.",
            "Do not replace work with generic method summaries.",
            "If tools are available and needed, emit a tool-call envelope exactly as specified.",
            "If no tool call is needed, answer normally and do not mention this bridge."
        ]);
    }

    private static string BuildToolContract(ProviderRequest request)
    {
        if (request.Tools is null || request.Tools.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>
        {
            "Tool contract:",
            "When a tool is required, respond only with <chat2api_tool_calls> JSON </chat2api_tool_calls>.",
            "The JSON must be an array of calls: [{\"name\":\"tool_name\",\"arguments\":{\"key\":\"value\"}}].",
            "Do not explain the call. Do not wrap it in Markdown. Do not continue with final text until tool results are provided.",
            $"Available tools: {request.Tools.ToJsonString()}"
        };

        if (request.ToolChoice is not null)
        {
            parts.Add($"Requested tool choice: {request.ToolChoice.ToJsonString()}");
        }

        return string.Join('\n', parts);
    }

    private static string BuildControls(ProviderRequest request)
    {
        return string.Join('\n', [
            $"Mode: {request.Mode}",
            $"Deep thinking: {(request.Thinking ? "on" : "off")}",
            $"Web search: {(request.WebSearch ? "on" : "off")}",
            $"Conversation id: {request.ConversationId ?? "ephemeral"}"
        ]);
    }

    private static string FormatMessage(ApiMessage message)
    {
        var name = string.IsNullOrWhiteSpace(message.Name) ? string.Empty : $" name={message.Name}";
        var tool = string.IsNullOrWhiteSpace(message.ToolCallId) ? string.Empty : $" tool_call_id={message.ToolCallId}";
        return $"<{message.Role}{name}{tool}>\n{message.Content}\n</{message.Role}>";
    }

    private static string FormatAssistantResult(ProviderResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Content))
        {
            parts.Add(result.Content);
        }

        if (result.ToolCalls?.Count > 0)
        {
            parts.Add("Assistant tool calls:\n" + JsonSerializer.Serialize(result.ToolCalls));
        }

        return string.Join("\n", parts);
    }

    private static List<ApiMessage> MergeMessages(List<ApiMessage> stored, List<ApiMessage> incoming)
    {
        if (incoming.Count == 0)
        {
            return stored;
        }

        if (stored.Count == 0 || incoming.Count >= stored.Count)
        {
            return incoming;
        }

        var merged = stored.ToList();
        foreach (var message in incoming)
        {
            if (merged.Count == 0 || !SameMessage(merged[^1], message))
            {
                merged.Add(message);
            }
        }

        return merged;
    }

    private static bool SameMessage(ApiMessage left, ApiMessage right)
    {
        return string.Equals(left.Role, right.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
            && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal);
    }

    private static List<ApiMessage> TakeRecent(List<ApiMessage> messages, int tokenBudget, out int usedTokens)
    {
        var selected = new List<ApiMessage>();
        usedTokens = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var cost = TokenEstimator.Estimate(messages[i].Content);
            if (selected.Count > 0 && usedTokens + cost > tokenBudget)
            {
                break;
            }

            selected.Add(messages[i]);
            usedTokens += cost;
        }

        selected.Reverse();
        return selected;
    }

    private async Task<SummaryBuildResult> BuildIncrementalSummaryAsync(ProviderRequest request, string oldSummary, List<ApiMessage> newMessages, int tokenBudget, CancellationToken cancellationToken)
    {
        if (_summarizer is not null && newMessages.Count > 0)
        {
            var summary = oldSummary;
            var usedFallback = false;
            string? diagnostic = null;
            foreach (var chunk in ChunkSummaryMessages(newMessages, MaxSummaryInputTokens))
            {
                try
                {
                    var modelSummary = await _summarizer.SummarizeAsync(new IncrementalSummaryRequest(
                        Model: request.Model,
                        Mode: request.Mode,
                        OldSummary: summary,
                        NewMessages: chunk,
                        TokenBudget: tokenBudget), cancellationToken);
                    if (!string.IsNullOrWhiteSpace(modelSummary))
                    {
                        summary = TrimSummaryToBudget(modelSummary, tokenBudget);
                        continue;
                    }

                    _warn?.Invoke("incremental model summary returned no content; fallback=extractive");
                    usedFallback = true;
                    diagnostic ??= "summary_model_empty";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _warn?.Invoke($"incremental model summary unavailable; fallback=extractive; error={ex.GetType().Name}");
                    usedFallback = true;
                    diagnostic ??= "summary_model_unavailable";
                }

                summary = BuildExtractiveSummary(summary, chunk, tokenBudget);
            }

            return new SummaryBuildResult(
                summary,
                usedFallback ? "extractive_fallback" : "model",
                diagnostic);
        }

        var fallback = BuildExtractiveSummary(oldSummary, newMessages, tokenBudget);
        return new SummaryBuildResult(
            fallback,
            string.IsNullOrWhiteSpace(fallback) ? "none" : "extractive",
            null);
    }

    private static IEnumerable<List<ApiMessage>> ChunkSummaryMessages(IReadOnlyList<ApiMessage> messages, int tokenBudget)
    {
        var chunk = new List<ApiMessage>();
        var usedTokens = 0;
        foreach (var message in messages)
        {
            foreach (var segment in SplitMessageForSummary(message, tokenBudget))
            {
                var cost = TokenEstimator.Estimate(segment.Content);
                if (chunk.Count > 0 && usedTokens + cost > tokenBudget)
                {
                    yield return chunk;
                    chunk = [];
                    usedTokens = 0;
                }

                chunk.Add(segment);
                usedTokens += cost;
            }
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    private static IEnumerable<ApiMessage> SplitMessageForSummary(ApiMessage message, int tokenBudget)
    {
        if (TokenEstimator.Estimate(message.Content) <= tokenBudget)
        {
            yield return message;
            yield break;
        }

        var offset = 0;
        while (offset < message.Content.Length)
        {
            var length = Math.Min(message.Content.Length - offset, tokenBudget * 4);
            while (length > 1 && TokenEstimator.Estimate(message.Content.Substring(offset, length)) > tokenBudget)
            {
                length = Math.Max(1, length / 2);
            }

            yield return message with { Content = message.Content.Substring(offset, length) };
            offset += length;
        }
    }

    private static string? CombineDiagnostics(params string?[] diagnostics)
    {
        var values = diagnostics.Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic)).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? null : string.Join(';', values);
    }

    private static string BuildExtractiveSummary(string oldSummary, List<ApiMessage> older, int tokenBudget)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(oldSummary))
        {
            lines.Add(oldSummary);
        }

        foreach (var message in older)
        {
            var compact = Whitespace().Replace(message.Content, " ").Trim();
            if (compact.Length > 500)
            {
                compact = compact[..500] + "...";
            }

            lines.Add($"{message.Role}: {compact}");
        }

        return TrimSummaryToBudget(string.Join('\n', lines.Distinct()), tokenBudget);
    }

    private static string TrimSummaryToBudget(string summary, int tokenBudget)
    {
        while (TokenEstimator.Estimate(summary) > tokenBudget && summary.Length > 512)
        {
            summary = summary[^Math.Max(512, summary.Length * 3 / 4)..];
        }

        return summary;
    }

    private static List<ApiMessage> RetrieveRelevant(List<ApiMessage> candidates, string query, int tokenBudget, out int usedTokens)
    {
        var terms = Terms(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new List<ApiMessage>();
        usedTokens = 0;
        if (terms.Count == 0)
        {
            return selected;
        }

        foreach (var message in candidates
            .Select(message => new { Message = message, Score = Terms(message.Content).Count(terms.Contains) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(12))
        {
            var cost = TokenEstimator.Estimate(message.Message.Content);
            if (selected.Count > 0 && usedTokens + cost > tokenBudget)
            {
                break;
            }

            selected.Add(message.Message);
            usedTokens += cost;
        }

        return selected;
    }

    private static IEnumerable<string> Terms(string text)
    {
        return Term().Matches(text)
            .Select(match => match.Value)
            .Where(term => term.Length >= 2);
    }

    private async Task<string> ResolveConversationIdAsync(ProviderRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return request.ConversationId;
        }

        return await _store.FindContinuationIdAsync(request.Messages)
            ?? $"ephemeral-{Guid.NewGuid():N}";
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.Compiled)]
    private static partial Regex Term();

    private sealed record SummaryBuildResult(string Text, string Source, string? Diagnostic);
}

public sealed record PromptBudget(int TotalTokens, int SummaryTokens, int RetrievalTokens, int RecentTokens)
{
    public static PromptBudget ForMode(string mode, int? measuredContextTokens = null)
    {
        var total = measuredContextTokens is > 0
            ? Math.Max(2048, measuredContextTokens.Value)
            : (int)(ApiCatalog.Mode(mode).DefaultContextTokens * 0.9);
        var summary = Math.Max(512, (int)(total * 0.16));
        var retrieval = Math.Max(512, (int)(total * 0.22));
        var reserved = Math.Max(512, (int)(total * 0.125));
        var recent = Math.Max(512, total - summary - retrieval - reserved);
        return new PromptBudget(total, summary, retrieval, recent);
    }
}
