using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Chat2ApiTray.Services;

namespace Chat2ApiTray.Services.Context;

public interface IConversationGate
{
    Task<IAsyncDisposable> EnterAsync(string conversationId, CancellationToken cancellationToken);
}

public sealed class ConversationGate : IConversationGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcessGates = new(StringComparer.Ordinal);
    private readonly string _lockDirectory;

    public ConversationGate(string dataDirectory)
    {
        _lockDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "ConversationLocks");
        LocalDataDirectorySecurity.EnsurePrivateDirectory(_lockDirectory);
    }

    public async Task<IAsyncDisposable> EnterAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("Conversation id is required.", nameof(conversationId));
        }

        var gateKey = $"{_lockDirectory}\n{conversationId}";
        var gate = InProcessGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var lockStream = await AcquireProcessLockAsync(conversationId, cancellationToken);
            return new Lease(gate, lockStream);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(string conversationId, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
        var path = Path.Combine(_lockDirectory, $"{Convert.ToHexString(hash)}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly FileStream _lockStream;
        private int _disposed;

        public Lease(SemaphoreSlim gate, FileStream lockStream)
        {
            _gate = gate;
            _lockStream = lockStream;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lockStream.DisposeAsync();
            _gate.Release();
        }
    }
}
