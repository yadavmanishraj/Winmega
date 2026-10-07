namespace Omega.Core.Tests;

/// <summary>
/// Small hand-trimmed fixtures in the REAL upstream shapes, as verified
/// by live probes (UPSTREAM_VALIDATION): stringly-typed numbers,
/// play_count at song top level, list_count as a string, has_lyrics
/// accurate in search payloads but "false" in song.getDetails,
/// lyrics_id empty in search payloads, HTML entities in titles,
/// browse sections as direct arrays with unreliable entity types.
/// </summary>
internal static class Fixtures
{
    /// <summary>search.getResults payload (typed song search).</summary>
    public const string SearchSongsJson = """
        {
          "total": 4675,
          "start": 0,
          "results": [
            {
              "id": "aRZbUYD7",
              "title": "Tum Hi Ho",
              "subtitle": "Arijit Singh",
              "type": "song",
              "perma_url": "https://www.jiosaavn.com/song/tum-hi-ho/OQsZfzQ",
              "image": "http://c.saavncdn.com/430/Aashiqui-2-Hindi-2013-150x150.jpg",
              "language": "hindi",
              "year": "2013",
              "play_count": "60321486",
              "explicit_content": "0",
              "more_info": {
                "duration": "262",
                "label": "T-Series",
                "has_lyrics": "true",
                "lyrics_id": "",
                "album_id": "1123456",
                "album": "Aashiqui 2",
                "album_url": "https://www.jiosaavn.com/album/aashiqui-2/xYz",
                "encrypted_media_url": "AAAA",
                "320kbps": "true",
                "artistMap": {
                  "primary_artists": [
                    {
                      "id": "459320",
                      "name": "Arijit Singh",
                      "role": "primary_artists",
                      "type": "artist",
                      "image": "https://c.saavncdn.com/artists/Arijit-Singh-150x150.jpg",
                      "perma_url": "https://www.jiosaavn.com/artist/arijit-singh-songs/459320"
                    }
                  ],
                  "featured_artists": [],
                  "artists": []
                }
              }
            },
            {
              "id": "xYz12345",
              "title": "Gehra Hua (From &quot;Dhurandhar&quot;)",
              "type": "song",
              "image": "https://c.saavncdn.com/999/Test-150x150.jpg",
              "year": "2025",
              "explicit_content": "1",
              "more_info": { "duration": "301", "320kbps": "false" }
            }
          ]
        }
        """;

    /// <summary>
    /// song.getDetails payload. Note the trap (VALIDATION §3): the same
    /// song that has lyrics reports has_lyrics "false" here and carries
    /// no lyrics_id key at all.
    /// </summary>
    public const string SongDetailsJson = """
        {
          "songs": [
            {
              "id": "aRZbUYD7",
              "title": "Tum Hi Ho",
              "type": "song",
              "perma_url": "https://www.jiosaavn.com/song/tum-hi-ho/OQsZfzQ",
              "image": "https://c.saavncdn.com/430/Aashiqui-2-Hindi-2013-150x150.jpg",
              "language": "hindi",
              "year": "2013",
              "play_count": "60321486",
              "explicit_content": "0",
              "more_info": {
                "duration": "262",
                "has_lyrics": "false",
                "320kbps": "true"
              }
            }
          ]
        }
        """;

    /// <summary>
    /// content.getBrowseModules payload: five direct arrays plus the
    /// radio/top_shows wrapper objects. new_albums[0] deliberately has
    /// type "song" while being album-shaped (VALIDATION §4).
    /// </summary>
    public const string BrowseModulesJson = """
        {
          "new_trending": [
            {
              "id": "38682222",
              "title": "Bhediya",
              "subtitle": "Sachin-Jigar",
              "header_desc": "Album",
              "type": "album",
              "perma_url": "https://www.jiosaavn.com/album/bhediya/38682222",
              "image": "https://c.saavncdn.com/001/Bhediya-Hindi-2022-150x150.jpg",
              "language": "hindi",
              "list": [
                { "id": "sng00001", "title": "Thumkeshwari", "type": "song", "more_info": { "duration": "180" } }
              ]
            }
          ],
          "new_albums": [
            {
              "id": "40000001",
              "title": "Mislabelled Type Album",
              "type": "song",
              "image": "https://c.saavncdn.com/002/X-150x150.jpg",
              "list": []
            }
          ],
          "charts": [
            {
              "id": "802336660",
              "title": "Arijit Singh - Sad Songs - Hindi",
              "type": "playlist",
              "image": "https://c.saavncdn.com/003/Y-150x150.jpg"
            }
          ],
          "top_playlists": [],
          "browse_discover": [
            { "id": "chan1", "title": "Discover Weekly", "type": "channel" }
          ],
          "radio": {
            "featured_stations": [
              { "id": "st1", "title": "Hindi Classics", "type": "radio_station" }
            ]
          },
          "top_shows": {
            "shows": [ { "id": "show1", "title": "A Podcast", "type": "show" } ],
            "badge": "",
            "last_page": false
          }
        }
        """;

    /// <summary>
    /// content.getBrowseModules payload in the shape ACTUALLY served
    /// live: album/trending items carry "list": "" (an empty string, not
    /// an array) when no tracks are inlined. Regression fixture for the
    /// Home load failure this produced (the JSON value at $.list could
    /// not be converted to a song list).
    /// </summary>
    public const string BrowseModulesEmptyStringListJson = """
        {
          "new_trending": [
            {
              "id": "41000001",
              "title": "Trending Album Without Tracks",
              "type": "album",
              "perma_url": "https://www.jiosaavn.com/album/trending/41000001",
              "image": "https://c.saavncdn.com/010/T-150x150.jpg",
              "language": "hindi",
              "list": ""
            }
          ],
          "new_albums": [
            {
              "id": "41000002",
              "title": "New Album Without Tracks",
              "type": "album",
              "perma_url": "https://www.jiosaavn.com/album/new/41000002",
              "image": "https://c.saavncdn.com/011/N-150x150.jpg",
              "language": "hindi",
              "list": ""
            }
          ],
          "charts": [],
          "top_playlists": [],
          "browse_discover": [],
          "radio": { "featured_stations": [] },
          "top_shows": { "shows": [] }
        }
        """;

    /// <summary>Single browse item with no "list" key at all.</summary>
    public const string BrowseItemMissingListJson = """
        {
          "id": "43000001",
          "title": "No List Key",
          "type": "album",
          "image": "https://c.saavncdn.com/014/M-150x150.jpg"
        }
        """;

    /// <summary>Single browse item whose "list" is an object, not an array.</summary>
    public const string BrowseItemObjectListJson = """
        {
          "id": "43000002",
          "title": "Object List",
          "type": "album",
          "image": "https://c.saavncdn.com/015/O-150x150.jpg",
          "list": {}
        }
        """;

    /// <summary>Single album-shaped browse item with a real one-song "list" array.</summary>
    public const string BrowseItemRealListJson = """
        {
          "id": "43000003",
          "title": "Album With Tracks",
          "type": "album",
          "image": "https://c.saavncdn.com/016/R-150x150.jpg",
          "list": [
            { "id": "sng00002", "title": "Apna Time Aayega", "type": "song", "more_info": { "duration": "185" } }
          ]
        }
        """;

    /// <summary>
    /// content.getAlbumDetails payload for an album whose track list is
    /// served as "" upstream; the true total is in list_count/song_count.
    /// </summary>
    public const string AlbumEmptyStringListJson = """
        {
          "id": "42000001",
          "title": "Empty Album",
          "type": "album",
          "image": "https://c.saavncdn.com/012/E-150x150.jpg",
          "list_count": "12",
          "list": "",
          "more_info": { "song_count": "12" }
        }
        """;

    /// <summary>
    /// playlist.getDetails payload for a playlist whose song list is
    /// served as "" upstream; the true total is in list_count.
    /// </summary>
    public const string PlaylistEmptyStringListJson = """
        {
          "id": "pl000001",
          "title": "Empty Playlist",
          "type": "playlist",
          "image": "https://c.saavncdn.com/013/P-150x150.jpg",
          "list_count": "7",
          "list": "",
          "more_info": { "username": "JioSaavn" }
        }
        """;

    /// <summary>lyrics.getLyrics success body (lyrics_id = the song id).</summary>
    public const string LyricsJson = """
        {
          "lyrics": "Mujhko iraade de<br>Kasamein de, waade de",
          "lyrics_copyright": "Lyrics powered by JioSaavn",
          "snippet": "Mujhko iraade de"
        }
        """;

    /// <summary>lyrics.getLyrics body for a song without lyrics (HTTP 200 error body).</summary>
    public const string LyricsErrorJson = """
        { "status": "failure", "error": { "msg": "Something went wrong please try again" } }
        """;

    /// <summary>webradio.getSong body as currently served upstream (VALIDATION §6).</summary>
    public const string StationErrorJson = """
        { "stationid": "st-abc", "error": "No new song found for current radio." }
        """;

    /// <summary>webradio.createEntityStation success body.</summary>
    public const string StationCreatedJson = """
        { "stationid": "st-abc" }
        """;
}
