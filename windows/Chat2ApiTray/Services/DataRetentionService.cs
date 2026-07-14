using System.IO;
using Chat2ApiTray.Models;

namespace Chat2ApiTray.Services;

public static class DataRetentionService
{
    public static Task<IReadOnlyDictionary<string, int>> PruneAsync(
        string dataDirectory,
        TraySettings settings,
        CancellationToken cancellationToken = default)
    {
        var policies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Acceptance"] = Math.Max(1, settings.ShortRetentionDays),
            ["Diagnostics"] = Math.Max(1, settings.ShortRetentionDays),
            ["Uploads"] = Math.Max(1, settings.ShortRetentionDays),
            ["Conversations"] = Math.Max(1, settings.ConversationRetentionDays),
            ["ResponseContinuations"] = Math.Max(1, settings.ConversationRetentionDays),
            ["Logs"] = Math.Max(1, settings.LogRetentionDays)
        };
        var deleted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(dataDirectory, policy.Key);
            var count = 0;
            if (Directory.Exists(directory))
            {
                var cutoff = DateTime.UtcNow.AddDays(-policy.Value);
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(path);
                        count += 1;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            deleted[policy.Key] = count;
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(deleted);
    }
}
