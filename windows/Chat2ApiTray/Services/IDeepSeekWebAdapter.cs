using Chat2ApiTray.Services.Api;

namespace Chat2ApiTray.Services;

public interface IDeepSeekWebAdapter : IAsyncDisposable
{
    Task<AuthStatus> BeginLoginAsync(CancellationToken cancellationToken);

    Task<AuthStatus> AuthStatusAsync(string? message, CancellationToken cancellationToken);

    Task<string> SendAsync(string prompt, string mode, bool thinking, bool webSearch, CancellationToken cancellationToken);

    Task<string> SendAsync(string prompt, string mode, bool thinking, bool webSearch, IReadOnlyList<ProviderFile> files, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(string prompt, string mode, bool thinking, bool webSearch, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(string prompt, string mode, bool thinking, bool webSearch, IReadOnlyList<ProviderFile> files, CancellationToken cancellationToken);
}
