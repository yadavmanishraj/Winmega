using System.Text.Json;
using Omega.Core.Models;

namespace Omega.Core.Persistence;

/// <summary>One artist reference inside a <see cref="SongSnapshot"/> (mirrors <see cref="ArtistRef"/>).</summary>
public sealed record ArtistSnapshot(
    string Id,
    string Name,
    string? Role,
    string? ImageSmall,
    string? ImageMedium,
    string? ImageLarge,
    string? Url);

/// <summary>One stream-ladder rung inside a <see cref="SongSnapshot"/> (mirrors <see cref="QualityUrl"/>).</summary>
public sealed record StreamSnapshot(
    string Label,
    int Kbps,
    string Url);

/// <summary>
/// The persisted form of a <see cref="Song"/>: a full-fidelity snapshot
/// stored as JSON in a single TEXT column of the library tables, so
/// Favorites / Playlists / History render — and play — fully offline
/// without an upstream fetch (design §6.2). It carries every
/// <see cref="Song"/> field, including the decrypted stream ladder: the
/// domain model does not retain the encrypted media URL the Android DB
/// stores, so the ladder itself is the only complete thing to persist.
/// </summary>
/// <remarks>
/// Serialization goes exclusively through the source-generated
/// <see cref="PersistenceJsonContext"/> — never reflection (design §3.2).
/// </remarks>
public sealed record SongSnapshot(
    string Id,
    string Name,
    string? Subtitle,
    string? Url,
    string? ImageSmall,
    string? ImageMedium,
    string? ImageLarge,
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
    IReadOnlyList<ArtistSnapshot> PrimaryArtists,
    IReadOnlyList<ArtistSnapshot> FeaturedArtists,
    IReadOnlyList<ArtistSnapshot> AllArtists,
    IReadOnlyList<StreamSnapshot> StreamUrls,
    bool Is320Kbps)
{
    /// <summary>Builds a snapshot from a domain song, field for field.</summary>
    public static SongSnapshot FromSong(Song song) => new(
        Id: song.Id,
        Name: song.Name,
        Subtitle: song.Subtitle,
        Url: song.Url,
        ImageSmall: song.Image.Small,
        ImageMedium: song.Image.Medium,
        ImageLarge: song.Image.Large,
        Language: song.Language,
        Year: song.Year,
        PlayCount: song.PlayCount,
        Explicit: song.Explicit,
        DurationSeconds: song.DurationSeconds,
        ReleaseDate: song.ReleaseDate,
        Label: song.Label,
        HasLyrics: song.HasLyrics,
        Copyright: song.Copyright,
        AlbumId: song.AlbumId,
        AlbumName: song.AlbumName,
        AlbumUrl: song.AlbumUrl,
        PrimaryArtists: song.Artists.Primary.Select(ToSnapshot).ToList(),
        FeaturedArtists: song.Artists.Featured.Select(ToSnapshot).ToList(),
        AllArtists: song.Artists.All.Select(ToSnapshot).ToList(),
        StreamUrls: song.StreamUrls.Select(q => new StreamSnapshot(q.Label, q.Kbps, q.Url)).ToList(),
        Is320Kbps: song.Is320Kbps);

    /// <summary>Rebuilds the domain song exactly as it was stored.</summary>
    public Song ToSong() => new(
        Id: Id,
        Name: Name,
        Subtitle: Subtitle,
        Url: Url,
        Image: new ImageSet(ImageSmall, ImageMedium, ImageLarge),
        Language: Language,
        Year: Year,
        PlayCount: PlayCount,
        Explicit: Explicit,
        DurationSeconds: DurationSeconds,
        ReleaseDate: ReleaseDate,
        Label: Label,
        HasLyrics: HasLyrics,
        Copyright: Copyright,
        AlbumId: AlbumId,
        AlbumName: AlbumName,
        AlbumUrl: AlbumUrl,
        Artists: new ArtistGroups(
            Primary: PrimaryArtists.Select(ToArtistRef).ToList(),
            Featured: FeaturedArtists.Select(ToArtistRef).ToList(),
            All: AllArtists.Select(ToArtistRef).ToList()),
        StreamUrls: StreamUrls.Select(s => new QualityUrl(s.Label, s.Kbps, s.Url)).ToList(),
        Is320Kbps: Is320Kbps);

    /// <summary>Serializes through the source-generated persistence context — never reflection.</summary>
    internal string ToJson() =>
        JsonSerializer.Serialize(this, PersistenceJsonContext.Default.SongSnapshot);

    /// <summary>Deserializes a stored snapshot; corrupt JSON is a loud failure, not a silent skip.</summary>
    internal static SongSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.SongSnapshot)
        ?? throw new InvalidDataException("Stored song snapshot deserialized to null.");

    private static ArtistSnapshot ToSnapshot(ArtistRef artist) => new(
        Id: artist.Id,
        Name: artist.Name,
        Role: artist.Role,
        ImageSmall: artist.Image.Small,
        ImageMedium: artist.Image.Medium,
        ImageLarge: artist.Image.Large,
        Url: artist.Url);

    private static ArtistRef ToArtistRef(ArtistSnapshot artist) => new(
        Id: artist.Id,
        Name: artist.Name,
        Role: artist.Role,
        Image: new ImageSet(artist.ImageSmall, artist.ImageMedium, artist.ImageLarge),
        Url: artist.Url);
}
