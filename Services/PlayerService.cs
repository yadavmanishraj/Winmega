using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Omega.Core.Models;
using Omega.Core.Playback;
using Omega.Core.Upstream;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Omega.Services;

/// <summary>Repeat behaviour for auto-advance and manual skips (design §7.1).</summary>
public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>
/// Playback engine (design §7): owns the singleton
/// <see cref="MediaPlayer"/>, a MANUAL queue, and the SMTC wiring.
///
/// Why manual everything: <c>MediaPlaybackList</c> hides per-track
/// control, but upstream's synthesised quality ladder demands it —
/// on <c>MediaFailed</c> we (1) descend one rung and retry the track,
/// (2) at the lowest rung re-resolve the track once via
/// <c>song.getDetails</c> (fresh <c>encrypted_media_url</c>) and retry
/// the top rung, (3) mark the track failed (<see cref="TrackFailed"/>)
/// and auto-advance (design §7.2). SMTC is likewise driven manually
/// (<c>CommandManager.IsEnabled = false</c>) so media keys, the
/// taskbar thumbnail and the volume flyout all route through the same
/// transport methods the UI buttons call (design §7.3).
///
/// Stream URLs: a mapped <see cref="Song"/> already carries its
/// decrypted ladder (<see cref="Song.StreamUrls"/>, ascending; the
/// last rung is the best). Resolution "at play time" therefore means:
/// a song arriving with NO ladder (or a ladder that fails end-to-end)
/// is re-resolved through <see cref="JioSaavnClient.GetSongsByIdsAsync"/>
/// before/while it plays.
///
/// Threading: constructed on the UI thread (first resolved from the
/// composition root by the shell). Every public method is callable
/// from any thread — all player/queue mutation is funnelled onto the
/// captured <see cref="DispatcherQueue"/>, and
/// <see cref="StateChanged"/> is only ever raised there, as the
/// <see cref="IPlaybackGateway"/> contract promises. Position is
/// surfaced by a 500 ms <see cref="DispatcherQueueTimer"/> (design
/// §7.1) — batched, never per-frame.
/// </summary>
public sealed partial class PlayerService : IPlaybackGateway, IDisposable
{
    private readonly JioSaavnClient _client;
    private readonly MediaPlayer _player;
    private readonly SystemMediaTransportControls _smtc;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _positionTimer;
    private readonly DispatcherQueueTimer _sleepTimer;

    /// <summary>Queue indices in play order (identity unless shuffled, design §7.1).</summary>
    private readonly List<int> _playOrder = new();

    private MediaSource? _currentSource;
    private int _currentIndex = -1;
    private int _ladderIndex = -1;
    private bool _reresolved;
    private int _generation;
    private RepeatMode _repeat = RepeatMode.Off;
    private bool _shuffle;
    private TimeSpan _lastTickPosition = TimeSpan.MinValue;

    public PlayerService(JioSaavnClient client)
    {
        _client = client;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "PlayerService must first be created on the UI thread (it owns a DispatcherQueue timer).");

        _player = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Media,
        };
        // Manual SMTC (design §7.3): without this, the player's own
        // command manager would also answer transport buttons and the
        // two would fight over the same gestures.
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += OnSmtcButtonPressed;

        Queue.CollectionChanged += OnQueueCollectionChanged;

        _positionTimer = _dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(500);
        _positionTimer.IsRepeating = true;
        _positionTimer.Tick += OnPositionTick;
        _positionTimer.Start();

        _sleepTimer = _dispatcher.CreateTimer();
        _sleepTimer.IsRepeating = false;
        _sleepTimer.Tick += OnSleepTimerTick;
    }

    // ------------------------------------------------------------------
    // IPlaybackGateway state
    // ------------------------------------------------------------------

    public Song? CurrentSong { get; private set; }

    public bool IsPlaying =>
        _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position => _player.PlaybackSession.Position;

    public TimeSpan Duration
    {
        get
        {
            TimeSpan natural = _player.PlaybackSession.NaturalDuration;
            return natural == TimeSpan.MaxValue || natural < TimeSpan.Zero
                ? TimeSpan.Zero
                : natural;
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised (UI thread) when a track fails the whole ladder (§7.2
    /// step 3) — the message is user-presentable; the Now Playing page
    /// shows it in an InfoBar with a Next action.
    /// </summary>
    public event EventHandler<string>? TrackFailed;

    // ------------------------------------------------------------------
    // Service surface beyond the gateway (Now Playing page / settings)
    // ------------------------------------------------------------------

    /// <summary>The live queue. Reordering it (the page's ListView does) remaps the current index.</summary>
    public ObservableCollection<Song> Queue { get; } = new();

    /// <summary>Index of <see cref="CurrentSong"/> within <see cref="Queue"/>, or -1.</summary>
    public int CurrentQueueIndex => _currentIndex;

    /// <summary>The engine itself — the Now Playing page's MediaPlayerElement binds to it (design §7.1).</summary>
    public MediaPlayer Player => _player;

    public RepeatMode Repeat => _repeat;

    public bool Shuffle
    {
        get => _shuffle;
        set => Enqueue(() =>
        {
            if (_shuffle == value)
            {
                return;
            }

            _shuffle = value;
            RebuildPlayOrder();
            RaiseStateChanged();
        });
    }

    /// <summary>The armed sleep-timer duration, or null when off (design §7.1).</summary>
    public TimeSpan? SleepTimerDuration { get; private set; }

    public bool IsSleepTimerActive => SleepTimerDuration.HasValue;

    /// <summary>Current playback rate (0.5–2.0, design §7.1).</summary>
    public double PlaybackRate => _player.PlaybackSession.PlaybackRate;

    // ------------------------------------------------------------------
    // IPlaybackGateway transport
    // ------------------------------------------------------------------

    public Task PlayAsync(Song song, IReadOnlyList<Song>? queue = null, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                if (queue is not null)
                {
                    Queue.Clear();
                    foreach (Song item in queue)
                    {
                        Queue.Add(item);
                    }

                    int index = IndexOfSong(song.Id);
                    if (index < 0)
                    {
                        Queue.Add(song);
                        index = Queue.Count - 1;
                    }

                    StartTrackCore(index);
                }
                else
                {
                    int index = IndexOfSong(song.Id);
                    if (index >= 0)
                    {
                        StartTrackCore(index);
                    }
                    else
                    {
                        Queue.Clear();
                        Queue.Add(song);
                        StartTrackCore(0);
                    }
                }

                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Tap-to-jump from the queue UI.</summary>
    public void PlayQueueIndex(int index) => Enqueue(() => StartTrackCore(index));

    public void Pause() => Enqueue(() =>
    {
        _player.Pause();
        RaiseStateChanged();
    });

    public void Resume() => Enqueue(() =>
    {
        if (CurrentSong is not null)
        {
            _player.Play();
            RaiseStateChanged();
        }
    });

    public void TogglePlayPause() => Enqueue(() =>
    {
        if (CurrentSong is null)
        {
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        RaiseStateChanged();
    });

    public void Next() => Enqueue(() => AdvanceCore(auto: false));

    public void Previous() => Enqueue(PreviousCore);

    public void Seek(TimeSpan position) => Enqueue(() => SeekCore(position));

    // ------------------------------------------------------------------
    // Extras: repeat, sleep timer, rate
    // ------------------------------------------------------------------

    public void CycleRepeatMode() => Enqueue(() =>
    {
        _repeat = _repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        RaiseStateChanged();
    });

    /// <summary>Arms the sleep timer (pause at expiry) or clears it with null (design §7.1).</summary>
    public void SetSleepTimer(TimeSpan? duration) => Enqueue(() =>
    {
        if (duration is { } value && value > TimeSpan.Zero)
        {
            SleepTimerDuration = value;
            _sleepTimer.Stop();
            _sleepTimer.Interval = value;
            _sleepTimer.Start();
        }
        else
        {
            SleepTimerDuration = null;
            _sleepTimer.Stop();
        }

        RaiseStateChanged();
    });

    public void SetPlaybackRate(double rate) => Enqueue(() =>
    {
        _player.PlaybackSession.PlaybackRate = rate;
        RaiseStateChanged();
    });

    // ------------------------------------------------------------------
    // Track start + failure ladder (design §7.2)
    // ------------------------------------------------------------------

    /// <summary>UI-thread only: loads Queue[index] as the current track.</summary>
    private void StartTrackCore(int queueIndex)
    {
        if (queueIndex < 0 || queueIndex >= Queue.Count)
        {
            return;
        }

        _currentIndex = queueIndex;
        CurrentSong = Queue[queueIndex];
        _reresolved = false;
        _ladderIndex = -1;
        int generation = ++_generation;
        UpdateSmtcDisplay();
        RaiseStateChanged();
        _ = ResolveAndPlayAsync(generation);
    }

    /// <summary>
    /// Resolves the current track's ladder when it arrived without one,
    /// then applies the top rung. Runs async; every mutation re-enters
    /// the dispatcher and re-checks the generation, so a fast skip
    /// never lets a stale resolution clobber the new track.
    /// </summary>
    private async Task ResolveAndPlayAsync(int generation)
    {
        Song? song = CurrentSong;
        if (song is null)
        {
            return;
        }

        if (song.StreamUrls.Count == 0)
        {
            Song? fresh = await TryResolveAsync(song.Id).ConfigureAwait(false);
            if (fresh is not null && fresh.StreamUrls.Count > 0)
            {
                Enqueue(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    ReplaceCurrentWith(fresh);
                    _ladderIndex = fresh.StreamUrls.Count - 1;
                    ApplyRung(generation);
                });
                return;
            }

            FailTrack(generation, $"“{song.Name}” has no playable stream.");
            return;
        }

        Enqueue(() =>
        {
            if (generation != _generation || CurrentSong is null)
            {
                return;
            }

            _ladderIndex = CurrentSong.StreamUrls.Count - 1;
            ApplyRung(generation);
        });
    }

    /// <summary>UI-thread only: points the player at the current ladder rung and plays.</summary>
    private void ApplyRung(int generation)
    {
        if (generation != _generation || CurrentSong is null || _ladderIndex < 0)
        {
            return;
        }

        string url = CurrentSong.StreamUrls[_ladderIndex].Url;
        var source = MediaSource.CreateFromUri(new Uri(url));
        MediaSource? previous = _currentSource;
        _currentSource = source;
        _player.Source = source;
        previous?.Dispose();
        _player.Play();
        RaiseStateChanged();
    }

    /// <summary>MediaFailed ladder (design §7.2): descend → re-resolve once → fail + advance.</summary>
    private async Task RecoverFromFailureAsync(int generation, MediaPlayerFailedEventArgs args)
    {
        Song? song = CurrentSong;
        if (song is null || generation != _generation)
        {
            return;
        }

        // (1) Descend one rung and retry the same track.
        if (_ladderIndex > 0)
        {
            Enqueue(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                _ladderIndex--;
                ApplyRung(generation);
            });
            return;
        }

        // (2) Lowest rung reached: re-resolve once (fresh
        // encrypted_media_url) and retry from the preferred rung.
        if (!_reresolved)
        {
            _reresolved = true;
            Song? fresh = await TryResolveAsync(song.Id).ConfigureAwait(false);
            if (fresh is not null && fresh.StreamUrls.Count > 0)
            {
                Enqueue(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    ReplaceCurrentWith(fresh);
                    _ladderIndex = fresh.StreamUrls.Count - 1;
                    ApplyRung(generation);
                });
                return;
            }
        }

        // (3) Give up on this track.
        FailTrack(generation, $"Couldn't play “{song.Name}” ({args.Error}).");
    }

    private void FailTrack(int generation, string message)
    {
        Enqueue(() =>
        {
            if (generation != _generation)
            {
                return;
            }

            TrackFailed?.Invoke(this, message);
            if (!AdvanceCore(auto: true))
            {
                _player.Pause();
                RaiseStateChanged();
            }
        });
    }

    /// <summary>Best-effort fresh resolution; null on any failure (the ladder decides what happens next).</summary>
    private async Task<Song?> TryResolveAsync(string songId)
    {
        try
        {
            IReadOnlyList<Song> songs = await _client
                .GetSongsByIdsAsync(new[] { songId })
                .ConfigureAwait(false);
            return songs.Count > 0 ? songs[0] : null;
        }
        catch (Exception)
        {
            // Deliberately swallowed: an unresolvable track is a normal
            // ladder outcome (FailTrack), not an engine crash. There is
            // no logging infrastructure in the app yet to report to.
            return null;
        }
    }

    /// <summary>UI-thread only: swaps the current queue entry for its freshly resolved copy.</summary>
    private void ReplaceCurrentWith(Song fresh)
    {
        // CurrentSong first: the CollectionChanged handler re-derives
        // _currentIndex by locating CurrentSong in the queue.
        CurrentSong = fresh;
        if (_currentIndex >= 0 && _currentIndex < Queue.Count)
        {
            Queue[_currentIndex] = fresh;
        }

        UpdateSmtcDisplay();
    }

    // ------------------------------------------------------------------
    // Navigation within the queue
    // ------------------------------------------------------------------

    /// <summary>UI-thread only. Returns true when a track (re)started.</summary>
    private bool AdvanceCore(bool auto)
    {
        if (Queue.Count == 0 || _currentIndex < 0)
        {
            return false;
        }

        if (auto && _repeat == RepeatMode.One)
        {
            // Repeat-one: rewind and keep playing the same source.
            SeekCore(TimeSpan.Zero);
            _player.Play();
            RaiseStateChanged();
            return true;
        }

        int orderPosition = _playOrder.IndexOf(_currentIndex);
        if (orderPosition < 0)
        {
            return false;
        }

        if (orderPosition + 1 < _playOrder.Count)
        {
            StartTrackCore(_playOrder[orderPosition + 1]);
            return true;
        }

        if (_repeat == RepeatMode.All && _playOrder.Count > 0)
        {
            StartTrackCore(_playOrder[0]);
            return true;
        }

        return false;
    }

    /// <summary>UI-thread only.</summary>
    private void PreviousCore()
    {
        if (_currentIndex < 0)
        {
            return;
        }

        if (Position > TimeSpan.FromSeconds(3))
        {
            SeekCore(TimeSpan.Zero);
            return;
        }

        int orderPosition = _playOrder.IndexOf(_currentIndex);
        if (orderPosition > 0)
        {
            StartTrackCore(_playOrder[orderPosition - 1]);
        }
        else if (_repeat == RepeatMode.All && _playOrder.Count > 0)
        {
            StartTrackCore(_playOrder[^1]);
        }
        else
        {
            SeekCore(TimeSpan.Zero);
        }
    }

    /// <summary>UI-thread only.</summary>
    private void SeekCore(TimeSpan position)
    {
        MediaPlaybackSession session = _player.PlaybackSession;
        if (!session.CanSeek)
        {
            return;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        TimeSpan duration = Duration;
        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        session.Position = position;
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------
    // Queue bookkeeping
    // ------------------------------------------------------------------

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The current index follows the current SONG, not the slot —
        // this is what makes ListView drag-reorder safe mid-playback.
        if (CurrentSong is not null)
        {
            int index = IndexOfReference(CurrentSong);
            if (index >= 0)
            {
                _currentIndex = index;
            }
        }

        RebuildPlayOrder();
        RaiseStateChanged();
    }

    private void RebuildPlayOrder()
    {
        _playOrder.Clear();
        for (int i = 0; i < Queue.Count; i++)
        {
            _playOrder.Add(i);
        }

        if (_shuffle)
        {
            // Fisher–Yates; unshuffle simply restores the identity
            // order above, so the original sequence is never lost.
            for (int i = _playOrder.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (_playOrder[i], _playOrder[j]) = (_playOrder[j], _playOrder[i]);
            }
        }
    }

    private int IndexOfSong(string id)
    {
        for (int i = 0; i < Queue.Count; i++)
        {
            if (string.Equals(Queue[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfReference(Song song)
    {
        for (int i = 0; i < Queue.Count; i++)
        {
            if (ReferenceEquals(Queue[i], song))
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------
    // Engine + SMTC events
    // ------------------------------------------------------------------

    private void OnMediaEnded(MediaPlayer sender, object args) =>
        Enqueue(() => AdvanceCore(auto: true));

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        Enqueue(() =>
        {
            int generation = _generation;
            _ = RecoverFromFailureAsync(generation, args);
        });

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args) =>
        Enqueue(RaiseStateChanged);

    private void OnSmtcButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // Same transport methods the UI calls — one code path for
        // media keys, taskbar buttons and on-screen buttons alike.
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                Resume();
                break;
            case SystemMediaTransportControlsButton.Pause:
                Pause();
                break;
            case SystemMediaTransportControlsButton.Next:
                Next();
                break;
            case SystemMediaTransportControlsButton.Previous:
                Previous();
                break;
        }
    }

    private void OnPositionTick(DispatcherQueueTimer sender, object args)
    {
        TimeSpan position = Position;
        if (IsPlaying || position != _lastTickPosition)
        {
            _lastTickPosition = position;
            RaiseStateChanged();
        }
    }

    private void OnSleepTimerTick(DispatcherQueueTimer sender, object args)
    {
        SleepTimerDuration = null;
        _player.Pause();
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------
    // SMTC display/status + state notification (UI-thread only)
    // ------------------------------------------------------------------

    private void UpdateSmtcDisplay()
    {
        SystemMediaTransportControlsDisplayUpdater updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        Song? song = CurrentSong;
        updater.MusicProperties.Title = song?.Name ?? string.Empty;
        updater.MusicProperties.Artist = song?.PrimaryArtistNames ?? string.Empty;
        updater.MusicProperties.AlbumTitle = song?.AlbumName ?? string.Empty;
        // Artwork by URI reference: the §6.4 file cache does not exist
        // yet; when it lands this becomes a stream reference to the
        // cached file with no other change.
        string? artwork = song?.Image.Best;
        updater.Thumbnail = artwork is null
            ? null
            : RandomAccessStreamReference.CreateFromUri(new Uri(artwork));
        updater.Update();
    }

    private void UpdateSmtcStatus()
    {
        _smtc.PlaybackStatus = _player.PlaybackSession.PlaybackState switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped,
        };
    }

    private void RaiseStateChanged()
    {
        UpdateSmtcStatus();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    // Threading funnel
    // ------------------------------------------------------------------

    private void Enqueue(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _sleepTimer.Stop();
        _positionTimer.Tick -= OnPositionTick;
        _sleepTimer.Tick -= OnSleepTimerTick;
        Queue.CollectionChanged -= OnQueueCollectionChanged;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        _smtc.ButtonPressed -= OnSmtcButtonPressed;
        _currentSource?.Dispose();
        _player.Dispose();
    }
}
