using Chat2ApiTray.Services.Api;

namespace Chat2ApiTray.Services.Context;

public interface IIncrementalSummarizer
{
    Task<string> SummarizeAsync(IncrementalSummaryRequest request, CancellationToken cancellationToken);
}

public sealed record IncrementalSummaryRequest(
    string Model,
    string Mode,
    string OldSummary,
    List<ApiMessage> NewMessages,
    int TokenBudget);

public sealed class DelegateIncrementalSummarizer : IIncrementalSummarizer
{
    private readonly Func<IncrementalSummaryRequest, CancellationToken, Task<string>> _summarizeAsync;

    public DelegateIncrementalSummarizer(Func<IncrementalSummaryRequest, CancellationToken, Task<string>> summarizeAsync)
    {
        _summarizeAsync = summarizeAsync;
    }

    public Task<string> SummarizeAsync(IncrementalSummaryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _summarizeAsync(request, cancellationToken);
    }
}

public static class IncrementalSummaryPrompt
{
    public static string Build(IncrementalSummaryRequest request)
    {
        var messages = string.Join("\n\n", request.NewMessages.Select(message => $"<{message.Role}>\n{message.Content}\n</{message.Role}>"));
        return string.Join("\n\n", [
            "Update the rolling summary for a coding-agent conversation.",
            "Keep durable facts, tool outcomes, user constraints, filenames, commands, decisions, and unresolved errors.",
            "Do not add commentary. Return only the updated summary.",
            $"Token budget: {request.TokenBudget}",
            string.IsNullOrWhiteSpace(request.OldSummary) ? "Previous summary: <empty>" : $"Previous summary:\n{request.OldSummary}",
            $"New messages:\n{messages}"
        ]);
    }
}
