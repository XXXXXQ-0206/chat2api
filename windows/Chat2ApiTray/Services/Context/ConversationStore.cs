using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chat2ApiTray.Services;
using Chat2ApiTray.Services.Api;

namespace Chat2ApiTray.Services.Context;

public sealed partial class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;

    public ConversationStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "Conversations");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_directory);
    }

    public async Task<ConversationRecord> LoadAsync(string conversationId)
    {
        var path = PathFor(conversationId);
        var record = await ReadMatchingRecordAsync(path, conversationId);
        if (record is not null)
        {
            return record;
        }

        var legacyPath = LegacyPathFor(conversationId);
        if (!string.Equals(path, legacyPath, StringComparison.Ordinal))
        {
            var legacyRecord = await ReadMatchingRecordAsync(legacyPath, conversationId);
            if (legacyRecord is not null)
            {
                await AtomicJsonStateFile.WriteAsync(path, legacyRecord, JsonOptions);
                File.Delete(legacyPath);
                return legacyRecord;
            }
        }

        return new ConversationRecord { Id = conversationId, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public async Task SaveAsync(ConversationRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await AtomicJsonStateFile.WriteAsync(PathFor(record.Id), record, JsonOptions);
    }

    public async Task<string?> FindContinuationIdAsync(IReadOnlyList<ApiMessage> incoming)
    {
        if (incoming.Count == 0)
        {
            return null;
        }

        ConversationRecord? bestMatch = null;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var record = await AtomicJsonStateFile.ReadOrQuarantineAsync<ConversationRecord>(path, JsonOptions);
            if (record is null
                || !record.Id.StartsWith("ephemeral-", StringComparison.Ordinal)
                || record.Messages.Count == 0
                || record.Messages.Count > incoming.Count
                || !IsPrefix(record.Messages, incoming))
            {
                continue;
            }

            if (bestMatch is null
                || record.Messages.Count > bestMatch.Messages.Count
                || record.Messages.Count == bestMatch.Messages.Count && record.UpdatedAt > bestMatch.UpdatedAt)
            {
                bestMatch = record;
            }
        }

        return bestMatch?.Id;
    }

    private static bool IsPrefix(IReadOnlyList<ApiMessage> stored, IReadOnlyList<ApiMessage> incoming)
    {
        for (var index = 0; index < stored.Count; index += 1)
        {
            if (!SameMessage(stored[index], incoming[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameMessage(ApiMessage left, ApiMessage right)
    {
        return string.Equals(left.Role, right.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
            && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal);
    }

    private string PathFor(string conversationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
        return Path.Combine(_directory, $"{Convert.ToHexString(hash).ToLowerInvariant()}.json");
    }

    private string LegacyPathFor(string conversationId)
    {
        var safe = SafeFileName().Replace(conversationId, "_");
        return Path.Combine(_directory, $"{safe}.json");
    }

    private static async Task<ConversationRecord?> ReadMatchingRecordAsync(string path, string conversationId)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var record = await AtomicJsonStateFile.ReadOrQuarantineAsync<ConversationRecord>(path, JsonOptions);
        return record is not null && string.Equals(record.Id, conversationId, StringComparison.Ordinal) ? record : null;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+", RegexOptions.Compiled)]
    private static partial Regex SafeFileName();
}

public sealed class ConversationRecord
{
    public string Id { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public int SummarizedMessageCount { get; set; }

    public List<ApiMessage> Messages { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
