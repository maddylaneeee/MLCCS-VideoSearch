using System.Text.RegularExpressions;

namespace MLCCS.VideoSearch.Core.Privacy;

public static partial class DiagnosticRedactor
{
    public static string Redact(string input, string? userName = null)
    {
        var output = input;
        output = WindowsPath().Replace(output, "<media-path>");
        output = UnixUserPath().Replace(output, "<media-path>");
        if (!string.IsNullOrWhiteSpace(userName)) output = output.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        output = Bearer().Replace(output, "$1<secret>");
        output = KeyValueSecret().Replace(output, "$1=<secret>");
        output = QueryField().Replace(output, "$1<redacted-text>$3");
        return output;
    }

    public static bool ContainsLikelySecret(string text) => Bearer().IsMatch(text) || KeyValueSecret().IsMatch(text);

    [GeneratedRegex(@"(?i)\b[A-Z]:\\(?:[^\s\r\n<>:\""/\\|?*]+\\)+[^\s\r\n<>:\""/\\|?*]*")]
    private static partial Regex WindowsPath();
    [GeneratedRegex(@"/(?:Users|home)/[^/\s]+/(?:[^\s]+)")]
    private static partial Regex UnixUserPath();
    [GeneratedRegex(@"(?i)(authorization\s*:\s*bearer\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex Bearer();
    [GeneratedRegex(@"(?i)\b(api[_-]?key|token|password|secret)\s*=\s*[^\s;,]+")]
    private static partial Regex KeyValueSecret();
    [GeneratedRegex("(?i)(\\\"(?:query|transcript|ocrText)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")")]
    private static partial Regex QueryField();
}
