using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chat2ApiTray.Services;

namespace Chat2ApiTray.Services.Context;

public sealed partial class ResponseContinuationStore
{
    private readonly string _directory;

    public ResponseContinuationStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "ResponseContinuations");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_directory);
    }

    public async Task SaveAsync(string responseId, string conversationId, CancellationToken cancellationToken = default)
    {
        if (!ValidResponseId().IsMatch(responseId))
        {
            throw new ArgumentException("Invalid response id.", nameof(responseId));
        }

        var path = Path.Combine(_directory, $"{responseId}.json");
        await AtomicJsonStateFile.WriteAsync(path, new ResponseContinuation(responseId, conversationId), cancellationToken: cancellationToken);
    }

    public async Task<string?> ResolveAsync(string responseId, CancellationToken cancellationToken = default)
    {
        if (!ValidResponseId().IsMatch(responseId))
        {
            return null;
        }

        var path = Path.Combine(_directory, $"{responseId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var record = await AtomicJsonStateFile.ReadOrQuarantineAsync<ResponseContinuation>(path, cancellationToken: cancellationToken);
        return record?.ConversationId;
    }

    [GeneratedRegex(@"^resp_[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidResponseId();

    private sealed record ResponseContinuation(string ResponseId, string ConversationId);
}
