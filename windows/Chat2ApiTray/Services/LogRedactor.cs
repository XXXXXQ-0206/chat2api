using System.Text.RegularExpressions;

namespace Chat2ApiTray.Services;

public static partial class LogRedactor
{
    public static string Redact(string message)
    {
        var redacted = Authorization().Replace(message, "$1[REDACTED]");
        redacted = BearerToken().Replace(redacted, "Bearer [REDACTED]");
        redacted = Cookie().Replace(redacted, "$1: [REDACTED]");
        redacted = JsonSecret().Replace(redacted, "$1[REDACTED]$2");
        redacted = PromptOrBody().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    [GeneratedRegex(@"(?i)\b(authorization\s*:\s*(?:bearer|basic)\s+)[^\s,;]+")]
    private static partial Regex Authorization();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)\b(set-cookie|cookie)\s*:\s*[^\r\n;]+")]
    private static partial Regex Cookie();

    [GeneratedRegex("(?i)([\"']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token)[\"']?\\s*:\\s*[\"'])[^\"']*([\"'])")]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)\b(prompt|request\s+body)\s*(?:=|:)\s*[^\r\n]+")]
    private static partial Regex PromptOrBody();
}
