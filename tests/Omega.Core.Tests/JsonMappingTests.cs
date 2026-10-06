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
}
