using System.IO;
using System.Text.Json;
using Chat2ApiTray.Services;

namespace Chat2ApiTray.Services.Context;

internal static class AtomicJsonStateFile
{
    private const int ReplacementRetryCount = 7;
    private const int InitialReplacementRetryDelayMilliseconds = 25;

    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("State file must have a parent directory.", nameof(path));
        LocalDataDirectorySecurity.EnsurePrivateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceFileAsync(temporaryPath, path, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task ReplaceFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt += 1)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < ReplacementRetryCount)
            {
                var delay = TimeSpan.FromMilliseconds(InitialReplacementRetryDelayMilliseconds * (1 << attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public static async Task<T?> ReadOrQuarantineAsync<T>(string path, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (JsonException)
        {
            Quarantine(path);
            return default;
        }
    }

    private static void Quarantine(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var quarantinedPath = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, quarantinedPath);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
