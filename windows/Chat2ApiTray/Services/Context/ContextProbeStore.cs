using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chat2ApiTray.Services;

namespace Chat2ApiTray.Services.Context;

public sealed partial class ContextProbeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;

    public ContextProbeStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "ContextProbes");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_directory);
    }

    public async Task SaveAsync(ContextProbeRecord record)
    {
        await AtomicJsonStateFile.WriteAsync(PathFor(record.Mode), record, JsonOptions);
    }

    public async Task<List<ContextProbeRecord>> ReadAllAsync()
    {
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_directory);
        var records = new List<ContextProbeRecord>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var record = await AtomicJsonStateFile.ReadOrQuarantineAsync<ContextProbeRecord>(path, JsonOptions);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records.OrderBy(record => record.Mode, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string PathFor(string mode)
    {
        var safe = SafeFileName().Replace(mode, "_");
        return Path.Combine(_directory, $"{safe}.json");
    }

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+", RegexOptions.Compiled)]
    private static partial Regex SafeFileName();
}

public sealed record ContextProbeRecord(
    string Mode,
    int AcceptedChars,
    int EstimatedTokens,
    int EffectiveTokens,
    double SafetyRatio,
    string Source,
    DateTimeOffset MeasuredAt,
    string? Error);
