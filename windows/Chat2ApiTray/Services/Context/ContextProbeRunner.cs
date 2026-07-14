using Chat2ApiTray.Services.Api;

namespace Chat2ApiTray.Services.Context;

public sealed class ContextProbeRunner
{
    private readonly ContextProbeStore _store;
    private readonly Func<ContextProbeRequest, Task<ContextProbeAttempt>> _attemptAsync;

    public ContextProbeRunner(ContextProbeStore store, Func<ContextProbeRequest, Task<ContextProbeAttempt>> attemptAsync)
    {
        _store = store;
        _attemptAsync = attemptAsync;
    }

    public async Task<ContextProbeRecord> RunAsync(ContextProbeOptions options, CancellationToken cancellationToken)
    {
        var low = Math.Max(1, options.MinChars);
        var high = Math.Max(low, options.MaxChars);
        var best = 0;
        var firstRejected = 0;
        var contextLimitRejections = 0;
        var unknownError = (string?)null;

        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mid = low + (high - low) / 2;
            var prompt = BuildProbePrompt(options.Mode, mid);
            var attempt = await AttemptRepeatedAsync(options, prompt, cancellationToken);

            if (attempt.IsAccepted)
            {
                best = mid;
                low = mid + 1;
            }
            else if (attempt.IsExplicitContextLimit)
            {
                firstRejected = firstRejected == 0 ? mid : firstRejected;
                contextLimitRejections += 1;
                high = mid - 1;
            }
            else
            {
                unknownError = attempt.UnknownError;
                break;
            }
        }

        var confirmationError = unknownError;
        if (confirmationError is null && best > 0 && contextLimitRejections >= 2)
        {
            confirmationError = await ConfirmBoundaryAsync(options, best, firstRejected, cancellationToken);
        }

        var confirmed = confirmationError is null && best > 0 && contextLimitRejections >= 2;
        var error = confirmed ? null : confirmationError ?? InconclusiveError(best, contextLimitRejections);
        var estimatedTokens = TokenEstimator.Estimate(BuildProbePrompt(options.Mode, best));
        var effectiveTokens = confirmed ? Math.Max(0, (int)(estimatedTokens * options.SafetyRatio)) : 0;
        var record = new ContextProbeRecord(
            Mode: options.Mode,
            AcceptedChars: best,
            EstimatedTokens: estimatedTokens,
            EffectiveTokens: effectiveTokens,
            SafetyRatio: options.SafetyRatio,
            Source: "csharp-host",
            MeasuredAt: DateTimeOffset.UtcNow,
            Error: error);
        await _store.SaveAsync(record);
        return record;
    }

    private async Task<string?> ConfirmBoundaryAsync(ContextProbeOptions options, int acceptedChars, int rejectedChars, CancellationToken cancellationToken)
    {
        var accepted = await AttemptRepeatedAsync(options, BuildProbePrompt(options.Mode, acceptedChars), cancellationToken);
        if (!accepted.IsAccepted)
        {
            return accepted.UnknownError;
        }

        var rejected = await AttemptRepeatedAsync(options, BuildProbePrompt(options.Mode, rejectedChars), cancellationToken);
        return rejected.IsExplicitContextLimit ? null : rejected.UnknownError;
    }

    private async Task<ContextProbeAttempt> AttemptRepeatedAsync(ContextProbeOptions options, string prompt, CancellationToken cancellationToken)
    {
        var attempts = new List<ContextProbeAttempt>();
        for (var repetition = 0; repetition < 2; repetition += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts.Add(await _attemptAsync(new ContextProbeRequest(
                Mode: options.Mode,
                Prompt: prompt,
                Thinking: options.Thinking,
                WebSearch: options.WebSearch)));
        }

        if (attempts.All(attempt => attempt.IsAccepted))
        {
            return ContextProbeAttempt.Accepted(prompt.Length);
        }

        if (attempts.All(attempt => attempt.IsExplicitContextLimit))
        {
            return ContextProbeAttempt.Rejected("context_length_exceeded");
        }

        if (attempts.Any(attempt => attempt.IsAccepted) || attempts.Select(attempt => attempt.FailureKind).Distinct().Count() > 1)
        {
            return ContextProbeAttempt.Rejected("probe_unstable");
        }

        return attempts[0];
    }

    private static string InconclusiveError(int acceptedChars, int contextLimitRejections)
    {
        if (contextLimitRejections == 0)
        {
            return "unknown:upper_bound_not_reached";
        }

        if (acceptedChars == 0)
        {
            return "unknown:context_limit_without_accepted_probe";
        }

        return "unknown:context_limit_not_repeated";
    }

    private static string BuildProbePrompt(string mode, int targetChars)
    {
        var prefix = $"chat2api context probe mode={mode}. Reply with OK only.\n";
        var fill = new string('测', Math.Max(0, targetChars - prefix.Length));
        return prefix + fill;
    }
}

public sealed record ContextProbeOptions(
    string Mode,
    int MinChars,
    int MaxChars,
    bool Thinking,
    bool WebSearch,
    double SafetyRatio);

public sealed record ContextProbeRequest(
    string Mode,
    string Prompt,
    bool Thinking,
    bool WebSearch);

public enum ContextProbeFailureKind
{
    None,
    ContextLengthExceeded,
    Timeout,
    Login,
    Dom,
    Network,
    Other,
    Unstable
}

public sealed record ContextProbeAttempt(bool IsAccepted, int AcceptedChars, string? Error)
{
    public ContextProbeFailureKind FailureKind => IsAccepted ? ContextProbeFailureKind.None : Classify(Error);

    public bool IsExplicitContextLimit => FailureKind == ContextProbeFailureKind.ContextLengthExceeded;

    public string UnknownError => FailureKind switch
    {
        ContextProbeFailureKind.Timeout => "unknown:timeout",
        ContextProbeFailureKind.Login => "unknown:login",
        ContextProbeFailureKind.Dom => "unknown:dom",
        ContextProbeFailureKind.Network => "unknown:network",
        ContextProbeFailureKind.Unstable => "unknown:unstable",
        ContextProbeFailureKind.ContextLengthExceeded => "unknown:context_limit_not_repeated",
        _ => "unknown:provider"
    };

    public static ContextProbeAttempt Accepted(int acceptedChars)
    {
        return new ContextProbeAttempt(true, acceptedChars, null);
    }

    public static ContextProbeAttempt Rejected(string error)
    {
        return new ContextProbeAttempt(false, 0, error);
    }

    private static ContextProbeFailureKind Classify(string? error)
    {
        var value = error?.Trim() ?? string.Empty;
        if (string.Equals(value, "context_length_exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return ContextProbeFailureKind.ContextLengthExceeded;
        }

        if (value.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ContextProbeFailureKind.Timeout;
        }

        if (value.Contains("login", StringComparison.OrdinalIgnoreCase)
            || value.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return ContextProbeFailureKind.Login;
        }

        if (value.Contains("playwright", StringComparison.OrdinalIgnoreCase)
            || value.Contains("selector", StringComparison.OrdinalIgnoreCase)
            || value.Contains("dom", StringComparison.OrdinalIgnoreCase))
        {
            return ContextProbeFailureKind.Dom;
        }

        if (value.Contains("network", StringComparison.OrdinalIgnoreCase)
            || value.Contains("http", StringComparison.OrdinalIgnoreCase)
            || value.Contains("socket", StringComparison.OrdinalIgnoreCase)
            || value.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            return ContextProbeFailureKind.Network;
        }

        if (value.Contains("unstable", StringComparison.OrdinalIgnoreCase)
            || value.Contains("inconsistent", StringComparison.OrdinalIgnoreCase))
        {
            return ContextProbeFailureKind.Unstable;
        }

        return ContextProbeFailureKind.Other;
    }
}
