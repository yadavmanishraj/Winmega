using Omega.Core.Models;

namespace Omega.Core.Playback;

/// <summary>
/// The playback seam every page talks to (design §7). Implemented by
/// the app project's <c>PlayerService</c> (MediaPlayer + manual queue +
/// manual SMTC); pages and view models never touch the engine directly,
/// so the engine stays swappable and test doubles stay trivial.
///
/// Threading contract: <see cref="StateChanged"/> is raised on the UI
/// thread, and the readable state (<see cref="CurrentSong"/>,
/// <see cref="IsPlaying"/>, <see cref="Position"/>,
/// <see cref="Duration"/>) is safe to read from a
/// <see cref="StateChanged"/> handler. Transport methods may be called
/// from any thread.
/// </summary>
public interface IPlaybackGateway
{
    /// <summary>The track currently loaded in the player, or null when idle.</summary>
    Song? CurrentSong { get; }

    /// <summary>True while the engine is actively playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Current playback position within <see cref="CurrentSong"/>.</summary>
    TimeSpan Position { get; }

    /// <summary>Natural duration of <see cref="CurrentSong"/> (Zero when unknown).</summary>
    TimeSpan Duration { get; }

    /// <summary>Raised (on the UI thread) whenever any playback state above changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Plays <paramref name="song"/>, optionally in the context of
    /// <paramref name="queue"/> (the queue is replaced; playback starts
    /// at the song's position in it). Without a queue, the song plays
    /// within the existing queue when already present, else as a
    /// single-track queue. Stream URLs are resolved at play time — a
    /// song without a decrypted ladder is re-resolved upstream first.
    /// </summary>
    Task PlayAsync(Song song, IReadOnlyList<Song>? queue = null, CancellationToken ct = default);

    /// <summary>Pauses playback (no-op when nothing is playing).</summary>
    void Pause();

    /// <summary>Resumes playback of the loaded track.</summary>
    void Resume();

    /// <summary>Pauses when playing, resumes when paused.</summary>
    void TogglePlayPause();

    /// <summary>Skips to the next track in the queue (manual skip ignores repeat-one).</summary>
    void Next();

    /// <summary>
    /// Goes back: restarts the current track when more than a few
    /// seconds in, otherwise skips to the previous track.
    /// </summary>
    void Previous();

    /// <summary>Seeks within the current track (clamped to its duration).</summary>
    void Seek(TimeSpan position);
}
