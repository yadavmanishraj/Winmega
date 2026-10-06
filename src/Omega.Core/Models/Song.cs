namespace Omega.Core.Models;

/// <summary>
/// A fully resolved, playable song. Mapped from the raw upstream song
/// object (UPSTREAM_SPEC §3.1); stream URLs are the decrypted quality
/// ladder (empty when the song has no encrypted media URL, i.e. it is
/// not playable).
/// </summary>
public sealed record Song(
    string Id,
    string Name,
    string? Subtitle,
    string? Url,
    ImageSet Image,
    string? Language,
    int? Year,
    long? PlayCount,
    bool Explicit,
    int? DurationSeconds,
    string? ReleaseDate,
    string? Label,
    bool HasLyrics,
    string? Copyright,
    string? AlbumId,
    string? AlbumName,
    string? AlbumUrl,
    ArtistGroups Artists,
    IReadOnlyList<QualityUrl> StreamUrls,
    bool Is320Kbps)
{
    /// <summary>Convenience: primary artist names joined for display.</summary>
    public string PrimaryArtistNames =>
        string.Join(", ", Artists.Primary.Select(a => a.Name));

    /// <summary>The stream URL for a requested quality label (e.g. "320kbps"), or null.</summary>
    public string? StreamUrlFor(string qualityLabel) =>
        StreamUrls.FirstOrDefault(q => q.Label == qualityLabel)?.Url;

    /// <summary>Highest available stream URL, or null when unplayable.</summary>
    public string? BestStreamUrl => StreamUrls.Count == 0 ? null : StreamUrls[^1].Url;
}
