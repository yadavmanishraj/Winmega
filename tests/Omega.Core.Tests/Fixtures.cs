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

    /// <summary>
    /// content.getAlbumDetails payload: full header (copyright_text and
    /// the real-bool is_dolby_content previously dropped by the model),
    /// one inlined track.
    /// </summary>
    public const string AlbumDetailsJson = """
        {
          "id": "38682222",
          "title": "Bhediya",
          "subtitle": "Sachin-Jigar",
          "header_desc": "Bhediya (Hindi) film soundtrack",
          "type": "album",
          "perma_url": "https://www.jiosaavn.com/album/bhediya/38682222",
          "image": "https://c.saavncdn.com/001/Bhediya-Hindi-2022-150x150.jpg",
          "language": "hindi",
          "year": "2023",
          "play_count": "1234567",
          "explicit_content": "0",
          "list_count": "6",
          "list": [
            { "id": "sng00001", "title": "Thumkeshwari", "type": "song", "more_info": { "duration": "180" } }
          ],
          "more_info": {
            "song_count": "6",
            "copyright_text": "℗ 2023 Zee Music Company",
            "is_dolby_content": true,
            "artistMap": {
              "primary_artists": [
                {
                  "id": "461968",
                  "name": "Sachin-Jigar",
                  "role": "primary_artists",
                  "type": "artist",
                  "image": "https://c.saavncdn.com/artists/Sachin-Jigar-150x150.jpg",
                  "perma_url": "https://www.jiosaavn.com/artist/sachin-jigar-songs/461968"
                }
              ],
              "featured_artists": [],
              "artists": []
            }
          }
        }
        """;

    /// <summary>
    /// playlist.getDetails payload (trimmed from the live probe of
    /// playlist 802336660): the owner is the internal handle
    /// "phulki_user" in username/uid but the display name "JioSaavn"
    /// in firstname; follower_count is plain digits while fan_count is
    /// pre-formatted; last_updated is epoch seconds as a string.
    /// </summary>
    public const string PlaylistDetailsJson = """
        {
          "id": "802336660",
          "title": "Arijit Singh - Sad Songs - Hindi",
          "subtitle": "Just Updated",
          "header_desc": "Hindi sad songs of Arijit Singh",
          "type": "playlist",
          "perma_url": "https://www.jiosaavn.com/featured/arijit-singh-sad-songs-hindi/802336660",
          "image": "https://c.saavncdn.com/003/Y-150x150.jpg",
          "list_count": "25",
          "list": "",
          "more_info": {
            "uid": "phulki_user",
            "username": "phulki_user",
            "firstname": "JioSaavn",
            "lastname": "",
            "follower_count": "366849",
            "fan_count": "366,839",
            "last_updated": "1791281247",
            "is_dolby_content": true
          }
        }
        """;

    /// <summary>
    /// artist.getArtistPageDetails payload (trimmed from the live probe
    /// of artist 459320, Arijit Singh): subtitle is the ready-made
    /// "Artist • N Listeners" byline; bio is a JSON-encoded string;
    /// singles and latest_release are album-LITE items (type "album",
    /// list "", no media fields) — not songs; the playlist rails carry
    /// their counts only in more_info.song_count.
    /// </summary>
    public const string ArtistPageJson = """
        {
          "artistId": "459320",
          "name": "Arijit Singh",
          "subtitle": "Artist • 9503254 Listeners",
          "image": "https://c.saavncdn.com/artists/Arijit-Singh-150x150.jpg",
          "follower_count": "107974103",
          "fan_count": "9503254",
          "type": "artist",
          "isVerified": true,
          "dominantLanguage": "hindi",
          "dominantType": "singer",
          "bio": "[{\"text\":\"Arijit Singh is an Indian playback singer.\",\"title\":\"Introduction\",\"sequence\":\"1\"}]",
          "dob": "25-04-1987",
          "wiki": "http://en.wikipedia.org/wiki/Arijit_Singh",
          "availableLanguages": ["hindi", "bengali", "unknown"],
          "urls": {
            "overview": "https://www.jiosaavn.com/artist/arijit-singh-songs/459320",
            "songs": "https://www.jiosaavn.com/artist/arijit-singh-songs/459320",
            "albums": "https://www.jiosaavn.com/artist/arijit-singh-songs/459320"
          },
          "topSongs": [
            {
              "id": "aRZbUYD7",
              "title": "Tum Hi Ho",
              "type": "song",
              "image": "https://c.saavncdn.com/430/Aashiqui-2-Hindi-2013-150x150.jpg",
              "more_info": { "duration": "262" }
            }
          ],
          "topAlbums": [
            {
              "id": "1123456",
              "title": "Aashiqui 2",
              "type": "album",
              "image": "https://c.saavncdn.com/430/Aashiqui-2-Hindi-2013-150x150.jpg",
              "year": "2013",
              "list": "",
              "more_info": { "song_count": "11" }
            }
          ],
          "latest_release": [
            {
              "id": "51000009",
              "title": "Newest Single Album",
              "type": "album",
              "image": "https://c.saavncdn.com/020/L-150x150.jpg",
              "year": "2026",
              "list": "",
              "more_info": { "song_count": "1" }
            }
          ],
          "singles": [
            {
              "id": "51000001",
              "title": "Kesariya (Single)",
              "type": "album",
              "image": "https://c.saavncdn.com/021/S-150x150.jpg",
              "year": "2022",
              "list": "",
              "more_info": { "song_count": "1" }
            }
          ],
          "dedicated_artist_playlist": [
            {
              "id": "pl100",
              "title": "Just Arijit Singh",
              "subtitle": "25 Songs",
              "type": "playlist",
              "image": "https://c.saavncdn.com/022/D-150x150.jpg",
              "list": "",
              "more_info": { "song_count": "25", "firstname": "JioSaavn" }
            }
          ],
          "featured_artist_playlist": [
            {
              "id": "pl200",
              "title": "Featured In: Bollywood Romance",
              "subtitle": "30 Songs",
              "type": "playlist",
              "image": "https://c.saavncdn.com/023/F-150x150.jpg",
              "list": "",
              "more_info": { "song_count": "30", "firstname": "JioSaavn" }
            }
          ],
          "similarArtists": [
            {
              "id": "881158",
              "name": "Atif Aslam",
              "perma_url": "https://www.jiosaavn.com/artist/atif-aslam-songs/881158",
              "image_url": "https://c.saavncdn.com/artists/Atif-Aslam-150x150.jpg",
              "type": "artist"
            }
          ]
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
