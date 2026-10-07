using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Omega.Core.Models;
using Omega.Core.Upstream;
using Omega.Core.Upstream.Dtos;
using Xunit;

namespace Omega.Core.Tests;

/// <summary>
/// Parsing + mapping tests over the hand-trimmed fixtures. They
/// deserialize ONLY through <see cref="UpstreamJsonContext"/> — the same
/// source-generated path the client uses — so a missing registration
/// fails here, not on a device.
/// </summary>
public class JsonMappingTests
{
    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(
            json,
            UpstreamJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
                ?? throw new InvalidOperationException(
                    $"No JsonTypeInfo registered for {typeof(T).Name} in UpstreamJsonContext."))!;

    [Fact]
    public void SearchSongs_ParsesStringlyTypedFields_AndMapsDomain()
    {
        RawPagedDto<RawSongDto> dto = Deserialize<RawPagedDto<RawSongDto>>(Fixtures.SearchSongsJson);
        var page = new PagedResult<Song>(
            dto.Total, dto.Results.Select(UpstreamMapper.MapSong).ToList());

        Assert.Equal(4675, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.False(page.IsComplete(page.Items.Count));
        Assert.True(page.IsComplete(4675));

        Song first = page.Items[0];
        Assert.Equal("aRZbUYD7", first.Id);
        Assert.Equal("Tum Hi Ho", first.Name);
        Assert.Equal(60321486L, first.PlayCount);       // top-level string -> long
        Assert.Equal(262, first.DurationSeconds);       // more_info string -> int
        Assert.Equal(2013, first.Year);
        Assert.False(first.Explicit);
        Assert.True(first.HasLyrics);                   // search payload flag is accurate
        Assert.True(first.Is320Kbps);
        Assert.Equal("Arijit Singh", first.PrimaryArtistNames);
        Assert.Equal("Aashiqui 2", first.AlbumName);

        // Image ladder: http forced to https, size tokens swapped.
        Assert.Equal("https://c.saavncdn.com/430/Aashiqui-2-Hindi-2013-50x50.jpg", first.Image.Small);
        Assert.Equal("https://c.saavncdn.com/430/Aashiqui-2-Hindi-2013-500x500.jpg", first.Image.Large);

        // The fixture's "AAAA" is not valid DES ciphertext: mapping must
        // degrade to an empty ladder (unplayable), never throw.
        Assert.Empty(first.StreamUrls);
        Assert.Null(first.BestStreamUrl);

        Song second = page.Items[1];
        Assert.Equal("Gehra Hua (From \"Dhurandhar\")", second.Name); // HTML entities decoded
        Assert.True(second.Explicit);                                  // "1" -> true
        Assert.False(second.Is320Kbps);
    }

    [Fact]
    public void SongDetails_HasLyricsIsFalse_AndLyricsIdAbsent_PerValidationTrap()
    {
        RawSongDetailsDto dto = Deserialize<RawSongDetailsDto>(Fixtures.SongDetailsJson);
        Song song = UpstreamMapper.MapSong(dto.Songs.Single());

        // Documents the trap: details payloads cannot gate lyrics.
        Assert.False(song.HasLyrics);
        Assert.Equal(262, song.DurationSeconds);
    }

    [Fact]
    public void BrowseModules_SectionsParse_ByShape_NotType()
    {
        // The modules payload is walked as a JsonElement DOM (design §3.2),
        // exactly as the client does — no section DTOs involved.
        using JsonDocument doc = JsonDocument.Parse(Fixtures.BrowseModulesJson);
        HomeModules home = UpstreamMapper.MapBrowseModules(doc.RootElement);

        Assert.Single(home.NewTrending);
        Assert.Single(home.NewTrending[0].Tracks); // album-shaped item carries its list
        Assert.True(home.NewTrending[0].HasTrackList);
        Assert.Equal("Bhediya", home.NewTrending[0].Title);

        // type says "song" but the item is album-shaped — shape wins.
        Assert.Single(home.NewAlbums);
        Assert.Equal("Mislabelled Type Album", home.NewAlbums[0].Title);

        Assert.Single(home.Charts);
        Assert.Empty(home.TopPlaylists);
        Assert.Single(home.BrowseDiscover);
        Assert.Single(home.RadioStations);  // unwrapped from featured_stations
        Assert.Single(home.TopShows);       // unwrapped from shows
        Assert.Equal(7, home.Sections.Count);
    }

    [Fact]
    public void BrowseModules_EmptyStringList_LoadsItems_WithNoTracks()
    {
        // Regression for the live Home failure: upstream serves
        // "list": "" on album/trending browse items, which used to throw
        // during DTO deserialization and take the whole feed down.
        using JsonDocument doc = JsonDocument.Parse(Fixtures.BrowseModulesEmptyStringListJson);
        HomeModules home = UpstreamMapper.MapBrowseModules(doc.RootElement);

        Assert.Single(home.NewTrending);
        Assert.Equal("Trending Album Without Tracks", home.NewTrending[0].Title);
        Assert.Empty(home.NewTrending[0].Tracks);
        Assert.False(home.NewTrending[0].HasTrackList);

        Assert.Single(home.NewAlbums);
        Assert.Equal("New Album Without Tracks", home.NewAlbums[0].Title);
        Assert.Empty(home.NewAlbums[0].Tracks);
        Assert.False(home.NewAlbums[0].HasTrackList);
    }

    [Fact]
    public void BrowseItem_MissingList_Loads_WithNoTracks()
    {
        RawBrowseItemDto dto = Deserialize<RawBrowseItemDto>(Fixtures.BrowseItemMissingListJson);

        Assert.Null(dto.List);
        HomeEntity entity = UpstreamMapper.MapBrowseItem(dto);
        Assert.Empty(entity.Tracks);
        Assert.False(entity.HasTrackList);
    }

    [Fact]
    public void BrowseItem_ObjectList_Loads_WithNoTracks()
    {
        RawBrowseItemDto dto = Deserialize<RawBrowseItemDto>(Fixtures.BrowseItemObjectListJson);

        HomeEntity entity = UpstreamMapper.MapBrowseItem(dto);
        Assert.Empty(entity.Tracks);
        Assert.False(entity.HasTrackList);
    }

    [Fact]
    public void BrowseItem_RealList_StillMapsTracks()
    {
        RawBrowseItemDto dto = Deserialize<RawBrowseItemDto>(Fixtures.BrowseItemRealListJson);

        Assert.NotNull(dto.List);
        Assert.Single(dto.List!);

        HomeEntity entity = UpstreamMapper.MapBrowseItem(dto);
        Assert.True(entity.HasTrackList);
        Assert.Single(entity.Tracks);
        Assert.Equal("Apna Time Aayega", entity.Tracks[0].Name);
        Assert.Equal(185, entity.Tracks[0].DurationSeconds);
    }

    [Fact]
    public void Album_EmptyStringList_Tolerated()
    {
        RawAlbumDto dto = Deserialize<RawAlbumDto>(Fixtures.AlbumEmptyStringListJson);
        Album album = UpstreamMapper.MapAlbum(dto);

        Assert.Empty(album.Songs);
        Assert.Equal(12, album.SongCount); // more_info.song_count — never the page
        Assert.Equal("Empty Album", album.Name);
    }

    [Fact]
    public void Playlist_EmptyStringList_Tolerated()
    {
        RawPlaylistDto dto = Deserialize<RawPlaylistDto>(Fixtures.PlaylistEmptyStringListJson);
        Playlist playlist = UpstreamMapper.MapPlaylist(dto);

        Assert.Empty(playlist.Songs);
        Assert.Equal(7, playlist.SongCount); // list_count is the true total
        Assert.Equal("JioSaavn", playlist.OwnerName);
    }

    [Fact]
    public void Album_CopyrightText_AndDolby_AreExposed()
    {
        RawAlbumDto dto = Deserialize<RawAlbumDto>(Fixtures.AlbumDetailsJson);
        Album album = UpstreamMapper.MapAlbum(dto);

        Assert.Equal("Bhediya", album.Name);
        Assert.Equal("℗ 2023 Zee Music Company", album.CopyrightText);
        Assert.True(album.IsDolbyContent);
        Assert.Equal(6, album.SongCount);
        Assert.Single(album.Songs);
        Assert.Equal("Sachin-Jigar", album.Artists.Primary.Single().Name);
    }

    [Fact]
    public void Playlist_OwnerDisplayName_Followers_LastUpdated_Dolby_AreExposed()
    {
        RawPlaylistDto dto = Deserialize<RawPlaylistDto>(Fixtures.PlaylistDetailsJson);
        Playlist playlist = UpstreamMapper.MapPlaylist(dto);

        // Display name wins over the internal "phulki_user" handle.
        Assert.Equal("JioSaavn", playlist.OwnerName);
        // Numeric follower_count wins; fan_count ("366,839") is ignored.
        Assert.Equal(366849L, playlist.FollowerCount);
        // Epoch-seconds string -> UTC instant (2026-10-06 per the probe).
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1791281247),
            playlist.LastUpdatedUtc);
        Assert.True(playlist.IsDolbyContent);
        Assert.Equal(25, playlist.SongCount);
        Assert.Equal("Hindi sad songs of Arijit Singh", playlist.Description);
    }

    [Fact]
    public void ArtistPage_NewSections_AreExposed()
    {
        RawArtistPageDto dto = Deserialize<RawArtistPageDto>(Fixtures.ArtistPageJson);
        Artist artist = UpstreamMapper.MapArtist(dto);

        Assert.Equal("459320", artist.Id);
        Assert.Equal("Artist • 9503254 Listeners", artist.Subtitle);
        Assert.Equal(9503254L, artist.FanCount);
        Assert.Equal(107974103L, artist.FollowerCount);
        Assert.True(artist.IsVerified);
        Assert.Equal("25-04-1987", artist.DateOfBirth);
        Assert.Equal("http://en.wikipedia.org/wiki/Arijit_Singh", artist.Wiki);

        // "unknown" is filtered; real languages survive in order.
        Assert.Equal(new[] { "hindi", "bengali" }, artist.AvailableLanguages);

        // Bio JSON string still decodes into titled entries.
        BioEntry bio = Assert.Single(artist.Bio);
        Assert.Equal("Introduction", bio.Title);

        Album latest = Assert.IsType<Album>(artist.LatestRelease);
        Assert.Equal("Newest Single Album", latest.Name);
        Assert.Equal(2026, latest.Year);
        Assert.Equal(1, latest.SongCount);
        Assert.Empty(latest.Songs); // album-lite: tracks load on demand

        Playlist dedicated = Assert.Single(artist.DedicatedPlaylists);
        Assert.Equal("Just Arijit Singh", dedicated.Name);
        Assert.Equal(25, dedicated.SongCount); // lite items: more_info.song_count

        Playlist featured = Assert.Single(artist.FeaturedPlaylists);
        Assert.Equal("Featured In: Bollywood Romance", featured.Name);
        Assert.Equal(30, featured.SongCount);

        Assert.Single(artist.TopSongs);
        Album topAlbum = Assert.Single(artist.TopAlbums);
        Assert.Equal(11, topAlbum.SongCount);
        Assert.Single(artist.SimilarArtists);
    }

    [Fact]
    public void ArtistPage_Singles_MapToAlbums_NotSongs()
    {
        RawArtistPageDto dto = Deserialize<RawArtistPageDto>(Fixtures.ArtistPageJson);
        Artist artist = UpstreamMapper.MapArtist(dto);

        // singles[] are album-lite releases upstream (type "album", no
        // streams/duration); as Songs they were unplayable shells.
        Album single = Assert.Single(artist.Singles);
        Assert.Equal("51000001", single.Id);
        Assert.Equal("Kesariya (Single)", single.Name);
        Assert.Equal(2022, single.Year);
        Assert.Equal(1, single.SongCount);
        Assert.Empty(single.Songs);
    }
}
