using System.Net;
using System.Text;
using Omega.Core.Models;
using Omega.Core.Upstream;
using Xunit;

namespace Omega.Core.Tests;

/// <summary>
/// End-to-end client tests over a stub HttpMessageHandler: they verify
/// the api.php request construction (fixed params, __call routing) and
/// response handling without any network.
/// </summary>
public class ClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _responder;

        public StubHandler(Func<string, string> responder) => _responder = responder;

        public string? LastQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string query = request.RequestUri?.Query ?? string.Empty;
            LastQuery = query;
            string body = _responder(query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (JioSaavnClient Client, StubHandler Handler) MakeClient(Func<string, string> responder)
    {
        var handler = new StubHandler(responder);
        var http = new HttpClient(handler);
        return (new JioSaavnClient(http, "https://example.invalid/api.php"), handler);
    }

    [Fact]
    public async Task SearchSongs_BuildsSpecRequest_AndMapsPage()
    {
        var (client, handler) = MakeClient(_ => Fixtures.SearchSongsJson);

        PagedResult<Song> page = await client.SearchSongsAsync("arijit singh", page: 0, pageSize: 5);

        Assert.Equal(4675, page.Total);
        Assert.Equal(2, page.Items.Count);
        string query = handler.LastQuery!;
        Assert.Contains("__call=search.getResults", query);
        Assert.Contains("_format=json", query);
        Assert.Contains("_marker=0", query);
        Assert.Contains("api_version=4", query);
        Assert.Contains("ctx=web6dot0", query);
        Assert.Contains("q=arijit%20singh", query);
        Assert.Contains("p=0", query);
        Assert.Contains("n=5", query);
    }

    [Fact]
    public async Task GetLyrics_UsesSongIdAsLyricsId_AndCleans()
    {
        var (client, handler) = MakeClient(_ => Fixtures.LyricsJson);

        LyricsResult? lyrics = await client.GetLyricsAsync("aRZbUYD7");

        Assert.NotNull(lyrics);
        Assert.Equal("Mujhko iraade de\nKasamein de, waade de", lyrics!.Lyrics);
        Assert.Equal("Lyrics powered by JioSaavn", lyrics.Copyright);
        // The corrected rule: lyrics_id is the song id, never more_info.lyrics_id.
        Assert.Contains("__call=lyrics.getLyrics", handler.LastQuery);
        Assert.Contains("lyrics_id=aRZbUYD7", handler.LastQuery);
    }

    [Fact]
    public async Task GetLyrics_ErrorBody_ReturnsNull()
    {
        var (client, _) = MakeClient(_ => Fixtures.LyricsErrorJson);
        Assert.Null(await client.GetLyricsAsync("yXCLyL-9"));
    }

    [Fact]
    public async Task GetSongsByIds_DetailsPayload_Maps()
    {
        var (client, handler) = MakeClient(_ => Fixtures.SongDetailsJson);

        IReadOnlyList<Song> songs = await client.GetSongsByIdsAsync(new[] { "aRZbUYD7", "1gHtmQ3x" });

        Assert.Single(songs);
        Assert.Contains("pids=aRZbUYD7%2C1gHtmQ3x", handler.LastQuery); // batch, comma-joined then escaped
    }

    [Fact]
    public async Task GetBrowseModules_MapsAllSections()
    {
        var (client, _) = MakeClient(_ => Fixtures.BrowseModulesJson);

        HomeModules home = await client.GetBrowseModulesAsync();

        Assert.Single(home.NewTrending);
        Assert.Single(home.RadioStations);
        Assert.Single(home.TopShows);
    }

    [Fact]
    public async Task StationSongs_UpstreamErrorBody_YieldsEmptyList()
    {
        var (client, handler) = MakeClient(query =>
            query.Contains("__call=webradio.createEntityStation", StringComparison.Ordinal)
                ? Fixtures.StationCreatedJson
                : Fixtures.StationErrorJson);

        string? stationId = await client.CreateStationAsync("aRZbUYD7");
        Assert.Equal("st-abc", stationId);
        Assert.Contains("ctx=android", handler.LastQuery);

        IReadOnlyList<Song> songs = await client.GetStationSongsAsync(stationId!, limit: 10);
        Assert.Empty(songs); // "No new song found for current radio." is a normal empty result
    }
}
