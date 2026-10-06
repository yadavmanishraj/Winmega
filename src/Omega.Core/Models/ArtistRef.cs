namespace Omega.Core.Models;

/// <summary>
/// A lightweight artist reference as embedded in songs, albums and
/// similar-artist lists (upstream "artistMap" entries).
/// </summary>
public sealed record ArtistRef(
    string Id,
    string Name,
    string? Role,
    ImageSet Image,
    string? Url);

/// <summary>The three artist groups upstream attaches to songs and albums.</summary>
public sealed record ArtistGroups(
    IReadOnlyList<ArtistRef> Primary,
    IReadOnlyList<ArtistRef> Featured,
    IReadOnlyList<ArtistRef> All)
{
    public static readonly ArtistGroups Empty =
        new(Array.Empty<ArtistRef>(), Array.Empty<ArtistRef>(), Array.Empty<ArtistRef>());
}

/// <summary>One paragraph of an artist biography (upstream bio is a JSON-encoded string of these).</summary>
public sealed record BioEntry(string? Text, string? Title, int Sequence);
