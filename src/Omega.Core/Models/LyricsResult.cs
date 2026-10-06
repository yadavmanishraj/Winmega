namespace Omega.Core.Models;

/// <summary>Cleaned song lyrics (line breaks already converted from &lt;br&gt;).</summary>
public sealed record LyricsResult(
    string Lyrics,
    string? Copyright,
    string? Snippet);
