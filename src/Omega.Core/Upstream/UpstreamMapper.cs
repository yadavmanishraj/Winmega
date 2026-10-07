using System.Globalization;
using System.Net;
using System.Text.Json;
using Omega.Core.Models;
using Omega.Core.Upstream.Dtos;

namespace Omega.Core.Upstream;

/// <summary>
/// Ports the jiosaavn-api repo's create*Payload helpers (song/album/
/// playlist/artist/search .helper.ts) to domain models: same field
/// selection, stringly-typed conversions, image/stream ladders, and
/// HTML-entity decoding for display (UPSTREAM_SPEC §8.5).
/// </summary>
public static class UpstreamMapper
{
    // ---------- shared conversions ----------

    private static string? Decode(string? value) =>
        string.IsNullOrEmpty(value) ? value : WebUtility.HtmlDecode(value);

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : null;

    private static int ParseCount(string? value) => ParseInt(value) ?? 0;

    /// <summary>Epoch-seconds string (playlist <c>last_updated</c>) → UTC instant.</summary>
    private static DateTimeOffset? ParseEpochSeconds(string? value)
    {
        long? seconds = ParseLong(value);
        // Sanity range: 1970 .. 2100 — anything else is a junk payload.
        return seconds is > 0 and <= 4102444800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value)
            : null;
    }

    // ---------- artists ----------

    public static ArtistRef MapArtistRef(RawArtistMapDto dto) => new(
        Id: dto.Id ?? string.Empty,
        Name: Decode(dto.Name) ?? string.Empty,
        Role: dto.Role,
        Image: MediaDecryptor.BuildImageSet(dto.Image),
        Url: dto.PermaUrl);

    public static ArtistGroups MapArtistGroups(RawArtistMapGroupDto? dto)
    {
        if (dto is null)
        {
            return ArtistGroups.Empty;
        }

        return new ArtistGroups(
            Primary: dto.PrimaryArtists.Select(MapArtistRef).ToList(),
            Featured: dto.FeaturedArtists.Select(MapArtistRef).ToList(),
            All: dto.Artists.Select(MapArtistRef).ToList());
    }

    // ---------- songs ----------

    public static Song MapSong(RawSongDto dto)
    {
        RawSongMoreInfoDto? info = dto.MoreInfo;
        bool is320 = info?.Is320Kbps == "true";
        return new Song(
            Id: dto.Id ?? string.Empty,
            Name: Decode(dto.Title) ?? string.Empty,
            Subtitle: Decode(dto.Subtitle),
            Url: dto.PermaUrl,
            Image: MediaDecryptor.BuildImageSet(dto.Image),
            Language: dto.Language,
            Year: ParseInt(dto.Year),
            PlayCount: ParseLong(dto.PlayCount),
            Explicit: dto.ExplicitContent == "1",
            DurationSeconds: ParseInt(info?.Duration),
            ReleaseDate: info?.ReleaseDate,
            Label: Decode(info?.Label),
            // Only meaningful when this DTO came from a search payload;
            // song.getDetails reports "false" unconditionally (VALIDATION §3).
            HasLyrics: info?.HasLyrics == "true",
            Copyright: info?.CopyrightText,
            AlbumId: info?.AlbumId,
            AlbumName: Decode(info?.Album),
            AlbumUrl: info?.AlbumUrl,
            Artists: MapArtistGroups(info?.ArtistMap),
            StreamUrls: MediaDecryptor.BuildQualityUrls(info?.EncryptedMediaUrl, is320),
            Is320Kbps: is320);
    }

    // ---------- albums / playlists ----------

    public static Album MapAlbum(RawAlbumDto dto)
    {
        int songCount = ParseCount(dto.MoreInfo?.SongCount);
        if (songCount == 0)
        {
            songCount = ParseCount(dto.ListCount);
        }

        if (songCount == 0)
        {
            songCount = dto.List.Count;
        }

        return new Album(
            Id: dto.Id ?? string.Empty,
            Name: Decode(dto.Title) ?? string.Empty,
            Description: Decode(dto.HeaderDesc),
            Url: dto.PermaUrl,
            Image: MediaDecryptor.BuildImageSet(dto.Image),
            Language: dto.Language,
            Year: ParseInt(dto.Year),
            PlayCount: ParseLong(dto.PlayCount),
            Explicit: dto.ExplicitContent == "1",
            SongCount: songCount,
            Artists: MapArtistGroups(dto.MoreInfo?.ArtistMap),
            Songs: dto.List.Select(MapSong).ToList(),
            CopyrightText: Decode(dto.MoreInfo?.CopyrightText),
            IsDolbyContent: dto.MoreInfo?.IsDolbyContent ?? false);
    }

    public static Playlist MapPlaylist(RawPlaylistDto dto)
    {
        RawPlaylistMoreInfoDto? info = dto.MoreInfo;

        // list_count is the true total (VALIDATION §5) — never the
        // page size. Playlist-lite items (search results, artist-page
        // rails) may carry only more_info.song_count instead.
        int songCount = ParseCount(dto.ListCount);
        if (songCount == 0)
        {
            songCount = ParseCount(info?.SongCount);
        }

        // Prefer the human display name (firstname/lastname) — the
        // username is an internal handle ("phulki_user") that editorial
        // playlists must not surface; it is only a fallback.
        string displayName = string.Join(" ", new[] { info?.Firstname, info?.Lastname }
            .Where(s => !string.IsNullOrEmpty(s)));
        string? owner = displayName.Length > 0 ? displayName : info?.Username;

        return new Playlist(
            Id: dto.Id ?? string.Empty,
            Name: Decode(dto.Title) ?? string.Empty,
            Description: Decode(dto.HeaderDesc) ?? Decode(dto.Description),
            Url: dto.PermaUrl,
            Image: MediaDecryptor.BuildImageSet(dto.Image),
            Language: dto.Language ?? info?.Language,
            Year: ParseInt(dto.Year),
            PlayCount: ParseLong(dto.PlayCount),
            SongCount: songCount,
            OwnerName: Decode(owner),
            Artists: (info?.Artists ?? new List<RawArtistMapDto>()).Select(MapArtistRef).ToList(),
            Songs: dto.List.Select(MapSong).ToList(),
            // follower_count is plain digits; fan_count is pre-formatted
            // ("366,839") and slightly divergent — never parse it.
            FollowerCount: ParseLong(info?.FollowerCount),
            LastUpdatedUtc: ParseEpochSeconds(info?.LastUpdated),
            IsDolbyContent: info?.IsDolbyContent ?? false);
    }

    // ---------- artist page ----------

    public static Artist MapArtist(RawArtistPageDto dto) => new(
        Id: dto.ArtistId ?? dto.Id ?? string.Empty,
        Name: Decode(dto.Name) ?? string.Empty,
        Url: dto.Urls?.Overview ?? dto.PermaUrl,
        Image: MediaDecryptor.BuildImageSet(dto.Image),
        FollowerCount: ParseLong(dto.FollowerCount),
        FanCount: ParseLong(dto.FanCount),
        IsVerified: dto.IsVerified,
        DominantLanguage: dto.DominantLanguage,
        DominantType: dto.DominantType,
        Bio: ParseBio(dto.Bio),
        DateOfBirth: dto.Dob,
        TopSongs: dto.TopSongs.Select(MapSong).ToList(),
        TopAlbums: dto.TopAlbums.Select(MapAlbum).ToList(),
        // Singles are album-lite releases upstream (type:"album", no
        // streams/duration) — mapping them as songs produced unplayable
        // shells. They are albums; detail loads via GetAlbumAsync.
        Singles: dto.Singles.Select(MapAlbum).ToList(),
        SimilarArtists: dto.SimilarArtists.Select(s => new ArtistRef(
            Id: s.Id ?? string.Empty,
            Name: Decode(s.Name) ?? string.Empty,
            Role: null,
            Image: MediaDecryptor.BuildImageSet(s.ImageUrl),
            Url: s.PermaUrl)).ToList(),
        Subtitle: Decode(dto.Subtitle),
        // latest_release arrives as a list of album-lite items; the
        // design renders at most one (the newest).
        LatestRelease: dto.LatestRelease.Count > 0 ? MapAlbum(dto.LatestRelease[0]) : null,
        DedicatedPlaylists: dto.DedicatedPlaylists.Select(MapPlaylist).ToList(),
        FeaturedPlaylists: dto.FeaturedPlaylists.Select(MapPlaylist).ToList(),
        AvailableLanguages: dto.AvailableLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !string.Equals(l, "unknown", StringComparison.OrdinalIgnoreCase))
            .ToList(),
        Wiki: dto.Wiki);

    /// <summary>
    /// The upstream bio is a JSON-encoded string of {text, title, sequence}.
    /// Parsed defensively (JsonDocument, no reflection): any failure falls
    /// back to treating the raw string as a single paragraph.
    /// </summary>
    private static IReadOnlyList<BioEntry> ParseBio(string? bioJson)
    {
        if (string.IsNullOrWhiteSpace(bioJson))
        {
            return Array.Empty<BioEntry>();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bioJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return SingleBio(bioJson);
            }

            var entries = new List<BioEntry>();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string? text = el.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String
                    ? Decode(t.GetString())
                    : null;
                string? title = el.TryGetProperty("title", out JsonElement ti) && ti.ValueKind == JsonValueKind.String
                    ? Decode(ti.GetString())
                    : null;
                int sequence = 0;
                if (el.TryGetProperty("sequence", out JsonElement seq))
                {
                    sequence = seq.ValueKind switch
                    {
                        JsonValueKind.Number => seq.TryGetInt32(out int n) ? n : 0,
                        JsonValueKind.String => ParseInt(seq.GetString()) ?? 0,
                        _ => 0,
                    };
                }

                entries.Add(new BioEntry(text, title, sequence));
            }

            return entries;
        }
        catch (JsonException)
        {
            return SingleBio(bioJson);
        }
    }

    private static IReadOnlyList<BioEntry> SingleBio(string raw) =>
        new[] { new BioEntry(Decode(raw), null, 0) };

    // ---------- global search ----------

    public static SearchResults MapSearchResults(RawGlobalSearchDto dto) => new(
        TopQuery: MapGlobalSongs(dto.TopQuery),
        Songs: MapGlobalSongs(dto.Songs),
        Albums: (dto.Albums?.Data ?? new List<RawGlobalAlbumItemDto>()).Select(a => new SearchItem(
            Id: a.Id ?? string.Empty,
            Title: Decode(a.Title) ?? string.Empty,
            Type: a.Type ?? "album",
            Url: a.PermaUrl,
            Image: MediaDecryptor.BuildImageSet(a.Image),
            Description: Decode(a.Description),
            AlbumName: null,
            PrimaryArtistsText: Decode(a.MoreInfo?.Music),
            SingersText: null,
            Language: a.MoreInfo?.Language,
            Year: a.MoreInfo?.Year,
            SongIds: a.MoreInfo?.SongPids)).ToList(),
        Artists: (dto.Artists?.Data ?? new List<RawGlobalArtistItemDto>()).Select(a => new SearchItem(
            Id: a.Id ?? string.Empty,
            Title: Decode(a.Title) ?? string.Empty,
            Type: a.Type ?? "artist",
            Url: null, // this section has no perma_url upstream
            Image: MediaDecryptor.BuildImageSet(a.Image),
            Description: Decode(a.Description),
            AlbumName: null,
            PrimaryArtistsText: null,
            SingersText: null,
            Language: null,
            Year: null,
            SongIds: null)).ToList(),
        Playlists: (dto.Playlists?.Data ?? new List<RawGlobalPlaylistItemDto>()).Select(p => new SearchItem(
            Id: p.Id ?? string.Empty,
            Title: Decode(p.Title) ?? string.Empty,
            Type: p.Type ?? "playlist",
            Url: p.PermaUrl,
            Image: MediaDecryptor.BuildImageSet(p.Image),
            Description: Decode(p.Description),
            AlbumName: null,
            PrimaryArtistsText: null,
            SingersText: null,
            Language: p.MoreInfo?.Language,
            Year: null,
            SongIds: null)).ToList());

    private static IReadOnlyList<SearchItem> MapGlobalSongs(RawSectionDto<RawGlobalSongItemDto>? section) =>
        (section?.Data ?? new List<RawGlobalSongItemDto>()).Select(s => new SearchItem(
            Id: s.Id ?? string.Empty,
            Title: Decode(s.Title) ?? string.Empty,
            Type: s.Type ?? "song",
            Url: s.PermaUrl,
            Image: MediaDecryptor.BuildImageSet(s.Image),
            Description: Decode(s.Description),
            AlbumName: Decode(s.MoreInfo?.Album),
            PrimaryArtistsText: Decode(s.MoreInfo?.PrimaryArtists),
            SingersText: Decode(s.MoreInfo?.Singers),
            Language: s.MoreInfo?.Language,
            Year: null,
            SongIds: null)).ToList();

    // ---------- home / browse modules ----------
    // The modules payload is walked manually as a JsonElement DOM
    // (design §3.2): section structure is hostile — five direct arrays,
    // two wrapper objects, unreliable entity "type" values — while the
    // per-item shape is stable and goes through the typed DTO + context.

    public static HomeEntity MapBrowseItem(RawBrowseItemDto dto) => new(
        Id: dto.Id ?? string.Empty,
        Title: Decode(dto.Title) ?? string.Empty,
        Subtitle: Decode(dto.Subtitle),
        HeaderDescription: Decode(dto.HeaderDesc),
        RawType: dto.Type ?? string.Empty,
        Url: dto.PermaUrl,
        Image: MediaDecryptor.BuildImageSet(dto.Image),
        Language: dto.Language,
        Tracks: (dto.List ?? new List<RawSongDto>()).Select(MapSong).ToList());

    /// <summary>Maps one section array element-by-element; non-array input yields an empty list.</summary>
    public static IReadOnlyList<HomeEntity> MapBrowseSection(JsonElement sectionArray)
    {
        if (sectionArray.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HomeEntity>();
        }

        var items = new List<HomeEntity>();
        foreach (JsonElement element in sectionArray.EnumerateArray())
        {
            RawBrowseItemDto? dto = element.Deserialize(UpstreamJsonContext.Default.RawBrowseItemDto);
            if (dto is not null)
            {
                items.Add(MapBrowseItem(dto));
            }
        }

        return items;
    }

    /// <summary>Walks a <c>content.getBrowseModules</c> root object into the Home feed.</summary>
    public static HomeModules MapBrowseModules(JsonElement root)
    {
        return new HomeModules(
            NewTrending: Section(root, "new_trending"),
            NewAlbums: Section(root, "new_albums"),
            Charts: Section(root, "charts"),
            TopPlaylists: Section(root, "top_playlists"),
            BrowseDiscover: Section(root, "browse_discover"),
            RadioStations: WrappedSection(root, "radio", "featured_stations"),
            TopShows: WrappedSection(root, "top_shows", "shows"));

        static IReadOnlyList<HomeEntity> Section(JsonElement rootElement, string key) =>
            rootElement.ValueKind == JsonValueKind.Object &&
            rootElement.TryGetProperty(key, out JsonElement section)
                ? MapBrowseSection(section)
                : Array.Empty<HomeEntity>();

        static IReadOnlyList<HomeEntity> WrappedSection(JsonElement rootElement, string key, string innerKey) =>
            rootElement.ValueKind == JsonValueKind.Object &&
            rootElement.TryGetProperty(key, out JsonElement wrapper) &&
            wrapper.ValueKind == JsonValueKind.Object &&
            wrapper.TryGetProperty(innerKey, out JsonElement inner)
                ? MapBrowseSection(inner)
                : Array.Empty<HomeEntity>();
    }
}
