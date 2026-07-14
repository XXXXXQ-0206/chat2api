using System.Text.RegularExpressions;

namespace Chat2ApiTray.Services.Context;

public static partial class TokenEstimator
{
    public static int Estimate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var asciiWords = AsciiWord().Matches(text).Count;
        var cjkChars = CjkChar().Matches(text).Count;
        var remaining = Math.Max(0, text.Length - asciiWords * 4 - cjkChars);
        return Math.Max(1, asciiWords + cjkChars + remaining / 4);
    }

    [GeneratedRegex(@"[A-Za-z0-9_]+", RegexOptions.Compiled)]
    private static partial Regex AsciiWord();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.Compiled)]
    private static partial Regex CjkChar();
}
