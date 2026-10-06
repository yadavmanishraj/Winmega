using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Omega.Core.Models;
using Omega.Core.Upstream.Dtos;

namespace Omega.Core.Upstream;

/// <summary>
/// Direct client for JioSaavn's upstream endpoint, replicating
/// jiosaavn-api's fetch helper exactly (UPSTREAM_SPEC §1): every call is
/// a GET to <c>https://www.jiosaavn.com/api.php</c> multiplexed by
/// <c>__call</c>, with fixed params <c>_format=json, _marker=0,
/// api_version=4, ctx=web6dot0</c> (ctx=android for radio only), and a
/// random browser User-Agent per request. No cookies, no auth.
/// All JSON goes through <see cref="UpstreamJsonContext"/> (source-generated).
/// </summary>
public sealed class JioSaavnClient : IDisposable
{
    public const string DefaultEndpoint = "https://www.jiosaavn.com/api.php";

    private const string CtxWeb = "web6dot0";
    private const string CtxAndroid = "android";

    // Subset of the repo's ~100-entry browser UA pool
    // (src/common/constants/user-agents.constant.ts); one is picked at
    // random per request, like fetch.helper.ts:28.
    private static readonly string[] UserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36",
        "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _endpoint;

    public JioSaavnClient(HttpClient? httpClient = null, string endpoint = DefaultEndpoint)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
        _ownsClient = httpClient is null;
        // Design §5.1: one HttpClient over SocketsHttpHandler when the
        // composition root does not supply its own singleton.
        _httpClient = httpClient ?? new HttpClient(
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    // ------------------------------------------------------------------
    // Search
    // ------------------------------------------------------------------

    /// <summary>Global search (<c>autocomplete.get</c>) — lightweight, non-playable items.</summary>
    public async Task<SearchResults> SearchAllAsync(string query, CancellationToken cancellationToken = default)
    {
        RawGlobalSearchDto dto = await GetAsync(
            "autocomplete.get", CtxWeb, TypeInfo<RawGlobalSearchDto>(),
            cancellationToken, ("query", query)).ConfigureAwait(false);
        return UpstreamMapper.MapSearchResults(dto);
    }

    /// <summary>Paged, playable song search (<c>search.getResults</c>). Page is 0-based.</summary>
    public async Task<PagedResult<Song>> SearchSongsAsync(
        string query, int page = 0, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        RawPagedDto<RawSongDto> dto = await GetAsync(
            "search.getResults", CtxWeb, TypeInfo<RawPagedDto<RawSongDto>>(),
            cancellationToken,
            ("q", query), ("p", Num(page)), ("n", Num(pageSize))).ConfigureAwait(false);
        return new PagedResult<Song>(dto.Total, dto.Results.Select(UpstreamMapper.MapSong).ToList());
    }

    /// <summary>Paged album search (<c>search.getAlbumResults</c>); albums come without tracks resolved for playback.</summary>
    public async Task<PagedResult<Album>> SearchAlbumsAsync(
        string query, int page = 0, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        RawPagedDto<RawAlbumDto> dto = await GetAsync(
            "search.getAlbumResults", CtxWeb, TypeInfo<RawPagedDto<RawAlbumDto>>(),
            cancellationToken,
            ("q", query), ("p", Num(page)), ("n", Num(pageSize))).ConfigureAwait(false);
        return new PagedResult<Album>(dto.Total, dto.Results.Select(UpstreamMapper.MapAlbum).ToList());
    }

    /// <summary>Paged artist search (<c>search.getArtistResults</c>).</summary>
    public async Task<PagedResult<ArtistRef>> SearchArtistsAsync(
        string query, int page = 0, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        RawPagedDto<RawArtistMapDto> dto = await GetAsync(
            "search.getArtistResults", CtxWeb, TypeInfo<RawPagedDto<RawArtistMapDto>>(),
            cancellationToken,
            ("q", query), ("p", Num(page)), ("n", Num(pageSize))).ConfigureAwait(false);
        return new PagedResult<ArtistRef>(dto.Total, dto.Results.Select(UpstreamMapper.MapArtistRef).ToList());
    }

    /// <summary>Paged playlist search (<c>search.getPlaylistResults</c>).</summary>
    public async Task<PagedResult<Playlist>> SearchPlaylistsAsync(
        string query, int page = 0, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        RawPagedDto<RawPlaylistDto> dto = await GetAsync(
            "search.getPlaylistResults", CtxWeb, TypeInfo<RawPagedDto<RawPlaylistDto>>(),
            cancellationToken,
            ("q", query), ("p", Num(page)), ("n", Num(pageSize))).ConfigureAwait(false);
        return new PagedResult<Playlist>(dto.Total, dto.Results.Select(UpstreamMapper.MapPlaylist).ToList());
    }

    // ------------------------------------------------------------------
    // Songs
    // ------------------------------------------------------------------

    /// <summary>
    /// Song details by id (<c>song.getDetails</c>, <c>pids</c> accepts a
    /// comma-separated batch). Throws <see cref="UpstreamException"/>
    /// when upstream returns no songs.
    /// </summary>
    public async Task<IReadOnlyList<Song>> GetSongsByIdsAsync(
        IEnumerable<string> songIds, CancellationToken cancellationToken = default)
    {
        string[] ids = songIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (ids.Length == 0)
        {
            return Array.Empty<Song>();
        }

        RawSongDetailsDto dto = await GetAsync(
            "song.getDetails", CtxWeb, TypeInfo<RawSongDetailsDto>(),
            cancellationToken, ("pids", string.Join(",", ids))).ConfigureAwait(false);
        if (dto.Songs.Count == 0)
        {
            throw new UpstreamException("Song not found (empty songs array).", "song.getDetails");
        }

        return dto.Songs.Select(UpstreamMapper.MapSong).ToList();
    }

    /// <summary>Resolves a JioSaavn song share-link token (<c>webapi.get?type=song</c>).</summary>
    public async Task<Song?> GetSongByLinkTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        RawSongDetailsDto dto = await GetAsync(
            "webapi.get", CtxWeb, TypeInfo<RawSongDetailsDto>(),
            cancellationToken, ("token", token), ("type", "song")).ConfigureAwait(false);
        RawSongDto? first = dto.Songs.FirstOrDefault();
        return first is null ? null : UpstreamMapper.MapSong(first);
    }

    // ------------------------------------------------------------------
    // Albums / playlists
    // ------------------------------------------------------------------

    /// <summary>Album details with full track list (<c>content.getAlbumDetails</c>).</summary>
    public async Task<Album> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)
    {
        RawAlbumDto dto = await GetAsync(
            "content.getAlbumDetails", CtxWeb, TypeInfo<RawAlbumDto>(),
            cancellationToken, ("albumid", albumId)).ConfigureAwait(false);
        if (dto.Id is null)
        {
            throw new UpstreamException("Album not found.", "content.getAlbumDetails");
        }

        return UpstreamMapper.MapAlbum(dto);
    }

    /// <summary>Resolves an album share-link token (<c>webapi.get?type=album</c>).</summary>
    public async Task<Album> GetAlbumByLinkTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        RawAlbumDto dto = await GetAsync(
            "webapi.get", CtxWeb, TypeInfo<RawAlbumDto>(),
            cancellationToken, ("token", token), ("type", "album")).ConfigureAwait(false);
        if (dto.Id is null)
        {
            throw new UpstreamException("Album not found.", "webapi.get");
        }

        return UpstreamMapper.MapAlbum(dto);
    }

    /// <summary>
    /// One page of a playlist (<c>playlist.getDetails</c>). The returned
    /// <see cref="Playlist.SongCount"/> is the true total (<c>list_count</c>);
    /// page until the accumulated songs reach it (VALIDATION §5).
    /// </summary>
    public async Task<Playlist> GetPlaylistAsync(
        string playlistId, int page = 0, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        RawPlaylistDto dto = await GetAsync(
            "playlist.getDetails", CtxWeb, TypeInfo<RawPlaylistDto>(),
            cancellationToken,
            ("listid", playlistId), ("n", Num(pageSize)), ("p", Num(page))).ConfigureAwait(false);
        if (dto.Id is null)
        {
            throw new UpstreamException("Playlist not found.", "playlist.getDetails");
        }

        return UpstreamMapper.MapPlaylist(dto);
    }

    /// <summary>Loads every page of a playlist until <c>list_count</c> songs are accumulated.</summary>
    public async Task<Playlist> GetPlaylistWithAllSongsAsync(
        string playlistId, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        Playlist first = await GetPlaylistAsync(playlistId, 0, pageSize, cancellationToken).ConfigureAwait(false);
        if (first.SongCount <= first.Songs.Count)
        {
            return first;
        }

        var all = new List<Song>(first.Songs);
        int page = 1;
        while (all.Count < first.SongCount)
        {
            Playlist next = await GetPlaylistAsync(playlistId, page, pageSize, cancellationToken).ConfigureAwait(false);
            if (next.Songs.Count == 0)
            {
                break; // short/empty page — upstream has no more
            }

            all.AddRange(next.Songs);
            page++;
        }

        return first with { Songs = all };
    }

    /// <summary>Resolves a playlist share-link token (<c>webapi.get?type=playlist</c>), one page.</summary>
    public async Task<Playlist> GetPlaylistByLinkTokenAsync(
        string token, int page = 0, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        RawPlaylistDto dto = await GetAsync(
            "webapi.get", CtxWeb, TypeInfo<RawPlaylistDto>(),
            cancellationToken,
            ("token", token), ("type", "playlist"), ("n", Num(pageSize)), ("p", Num(page))).ConfigureAwait(false);
        if (dto.Id is null)
        {
            throw new UpstreamException("Playlist not found.", "webapi.get");
        }

        return UpstreamMapper.MapPlaylist(dto);
    }

    // ------------------------------------------------------------------
    // Artists
    // ------------------------------------------------------------------

    /// <summary>Artist page (<c>artist.getArtistPageDetails</c>).</summary>
    public async Task<Artist> GetArtistAsync(
        string artistId,
        int songCount = 10,
        int albumCount = 10,
        int page = 0,
        string category = "popularity",
        string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        RawArtistPageDto dto = await GetAsync(
            "artist.getArtistPageDetails", CtxWeb, TypeInfo<RawArtistPageDto>(),
            cancellationToken,
            ("artistId", artistId),
            ("n_song", Num(songCount)),
            ("n_album", Num(albumCount)),
            ("page", Num(page)),
            ("sort_order", sortOrder),
            ("category", category)).ConfigureAwait(false);
        if (dto.ArtistId is null && dto.Id is null)
        {
            throw new UpstreamException("Artist not found.", "artist.getArtistPageDetails");
        }

        return UpstreamMapper.MapArtist(dto);
    }

    /// <summary>More artist songs (<c>artist.getArtistMoreSong</c>); page until accumulated ≥ total.</summary>
    public async Task<PagedResult<Song>> GetArtistSongsAsync(
        string artistId,
        int page = 0,
        string category = "popularity",
        string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        RawArtistSongsDto dto = await GetAsync(
            "artist.getArtistMoreSong", CtxWeb, TypeInfo<RawArtistSongsDto>(),
            cancellationToken,
            ("artistId", artistId),
            ("page", Num(page)),
            ("sort_order", sortOrder),
            ("category", category)).ConfigureAwait(false);
        RawArtistSongsPageDto? top = dto.TopSongs;
        return new PagedResult<Song>(
            top?.Total ?? 0,
            (top?.Songs ?? new List<RawSongDto>()).Select(UpstreamMapper.MapSong).ToList());
    }

    /// <summary>More artist albums (<c>artist.getArtistMoreAlbum</c>); page until accumulated ≥ total.</summary>
    public async Task<PagedResult<Album>> GetArtistAlbumsAsync(
        string artistId,
        int page = 0,
        string category = "popularity",
        string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        RawArtistAlbumsDto dto = await GetAsync(
            "artist.getArtistMoreAlbum", CtxWeb, TypeInfo<RawArtistAlbumsDto>(),
            cancellationToken,
            ("artistId", artistId),
            ("page", Num(page)),
            ("sort_order", sortOrder),
            ("category", category)).ConfigureAwait(false);
        RawArtistAlbumsPageDto? top = dto.TopAlbums;
        return new PagedResult<Album>(
            top?.Total ?? 0,
            (top?.Albums ?? new List<RawAlbumDto>()).Select(UpstreamMapper.MapAlbum).ToList());
    }

    // ------------------------------------------------------------------
    // Lyrics
    // ------------------------------------------------------------------

    /// <summary>
    /// Fetches lyrics for a song (<c>lyrics.getLyrics</c>).
    /// CORRECTED RULE (UPSTREAM_VALIDATION §3): the <c>lyrics_id</c>
    /// parameter is the <b>song id itself</b> — <c>more_info.lyrics_id</c>
    /// is empty/absent in real payloads and must never be used. Do not
    /// gate this call on the song-details payload's <c>has_lyrics</c>
    /// (always "false" there); the search payload's flag is the accurate
    /// one. Returns null when upstream's body has no lyrics.
    /// </summary>
    public async Task<LyricsResult?> GetLyricsAsync(string songId, CancellationToken cancellationToken = default)
    {
        RawLyricsDto dto = await GetAsync(
            "lyrics.getLyrics", CtxWeb, TypeInfo<RawLyricsDto>(),
            cancellationToken, ("lyrics_id", songId)).ConfigureAwait(false);
        string? cleaned = LyricsHelper.Clean(dto.Lyrics);
        return cleaned is null
            ? null
            : new LyricsResult(cleaned, dto.LyricsCopyright, dto.Snippet);
    }

    // ------------------------------------------------------------------
    // Home / trending
    // ------------------------------------------------------------------

    /// <summary>
    /// Home feed (<c>content.getBrowseModules</c>) — the only viable Home
    /// source. The payload is hostile/polysemous (mixed entity types,
    /// wrapped vs direct sections), so per design §3.2 it is walked
    /// manually as a <see cref="JsonElement"/> DOM — reflection-free —
    /// and only the per-item shape goes through the typed DTO.
    /// </summary>
    public async Task<HomeModules> GetBrowseModulesAsync(CancellationToken cancellationToken = default)
    {
        JsonElement root = await GetAsync(
            "content.getBrowseModules", CtxWeb, TypeInfo<JsonElement>(),
            cancellationToken).ConfigureAwait(false);
        return UpstreamMapper.MapBrowseModules(root);
    }

    /// <summary>Trending entities (<c>content.getTrending</c>) — top level is a bare array; types are mixed, branch on shape.</summary>
    public async Task<IReadOnlyList<HomeEntity>> GetTrendingAsync(CancellationToken cancellationToken = default)
    {
        JsonElement root = await GetAsync(
            "content.getTrending", CtxWeb, TypeInfo<JsonElement>(),
            cancellationToken).ConfigureAwait(false);
        return UpstreamMapper.MapBrowseSection(root);
    }

    // ------------------------------------------------------------------
    // Suggestions / radio (ctx=android)
    // WARNING (UPSTREAM_VALIDATION §6): step 2 currently answers every
    // station with {"error": "No new song found for current radio."}.
    // These methods model that as an empty result; do not build autoplay
    // on this flow until it is re-validated upstream.
    // ------------------------------------------------------------------

    /// <summary>Creates a radio station seeded by a song (<c>webradio.createEntityStation</c>).</summary>
    public async Task<string?> CreateStationAsync(string songId, CancellationToken cancellationToken = default)
    {
        // Repo derivation: entity_id = JSON.stringify([encodeURIComponent(songId)]),
        // then URL-encoded again as a query value by the URL builder.
        string entityId = "[\"" + Uri.EscapeDataString(songId) + "\"]";
        RawStationCreatedDto dto = await GetAsync(
            "webradio.createEntityStation", CtxAndroid, TypeInfo<RawStationCreatedDto>(),
            cancellationToken,
            ("entity_id", entityId), ("entity_type", "queue")).ConfigureAwait(false);
        return dto.StationId;
    }

    /// <summary>
    /// Fetches the next batch of station songs (<c>webradio.getSong</c>).
    /// The response's numeric-string keys are sorted numerically; the
    /// "stationid"/"error" keys are skipped, so the current upstream
    /// error body yields an empty list rather than an exception.
    /// </summary>
    public async Task<IReadOnlyList<Song>> GetStationSongsAsync(
        string stationId, int limit = 10, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> raw = await GetAsync(
            "webradio.getSong", CtxAndroid, TypeInfo<Dictionary<string, JsonElement>>(),
            cancellationToken,
            ("stationid", stationId), ("k", Num(limit))).ConfigureAwait(false);

        var songs = new List<Song>();
        foreach (KeyValuePair<string, JsonElement> entry in raw
            .Where(kv => int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .OrderBy(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture)))
        {
            RawStationEntryDto? parsed = entry.Value.Deserialize(UpstreamJsonContext.Default.RawStationEntryDto);
            if (parsed?.Song is not null)
            {
                songs.Add(UpstreamMapper.MapSong(parsed.Song));
            }
        }

        return songs.Take(limit).ToList();
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        UpstreamJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"No JsonTypeInfo registered for {typeof(T).Name} in UpstreamJsonContext.");

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string BuildUrl(string call, string ctx, (string Key, string Value)[] parameters)
    {
        var sb = new StringBuilder(_endpoint);
        sb.Append("?__call=").Append(Uri.EscapeDataString(call));
        sb.Append("&_format=json&_marker=0&api_version=4");
        sb.Append("&ctx=").Append(Uri.EscapeDataString(ctx));
        foreach ((string key, string value) in parameters)
        {
            sb.Append('&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return sb.ToString();
    }

    private async Task<T> GetAsync<T>(
        string call,
        string ctx,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken,
        params (string Key, string Value)[] parameters)
    {
        string url = BuildUrl(call, ctx, parameters);

        // Transport policy the upstream repo lacks (design §5.1): max 2
        // retries with exponential backoff, ONLY on 5xx / timeout / IO —
        // never on HTTP-200 "not found" bodies, which are valid answers.
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            string json;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(UserAgents[Random.Shared.Next(UserAgents.Length)]);
                request.Headers.Accept.ParseAdd("application/json");

                using HttpResponseMessage response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    if (status >= 500 && attempt < maxAttempts)
                    {
                        await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new UpstreamException($"Upstream returned HTTP {status} for {call}.", call);
                }

                json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxAttempts)
                {
                    await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new UpstreamException($"Network error calling {call}: {ex.Message}", call, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient timeout (not caller cancellation) — retryable.
                if (attempt < maxAttempts)
                {
                    await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new UpstreamException($"Timeout calling {call}.", call, ex);
            }

            T? result;
            try
            {
                result = JsonSerializer.Deserialize(json, typeInfo);
            }
            catch (JsonException ex)
            {
                throw new UpstreamException($"Malformed JSON from {call}: {ex.Message}", call, ex);
            }

            return result ?? throw new UpstreamException($"Empty response body from {call}.", call);
        }
    }

    private static Task DelayBeforeRetry(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (attempt - 1))), cancellationToken);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
