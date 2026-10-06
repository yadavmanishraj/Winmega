using System.Net;

namespace Omega.Core.Upstream;

/// <summary>
/// Lyrics cleaning (jiosaavn-dl's exact rule, confirmed in
/// UPSTREAM_VALIDATION §3): upstream separates lines with &lt;br&gt; only.
/// We additionally tolerate &lt;br/&gt; and &lt;br /&gt; defensively and
/// decode HTML entities for display.
/// </summary>
public static class LyricsHelper
{
    public static string? Clean(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return null;
        }

        string cleaned = rawLyrics
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);

        return WebUtility.HtmlDecode(cleaned).Trim();
    }
}
