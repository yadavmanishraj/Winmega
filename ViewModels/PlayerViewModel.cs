using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omega.Core.Models;
using Omega.Services;
using Windows.Media.Playback;

namespace Omega.ViewModels;

/// <summary>
/// Presentation state for the full Now Playing page (design §9.5).
/// A thin mirror over the singleton <see cref="PlayerService"/>: the
/// service raises <c>StateChanged</c> on the UI thread (500 ms ticker
/// + every transport/track change), and this model re-reads and
/// republishes bindable state. Transport commands forward straight
/// back to the service — the same methods the SMTC buttons invoke.
///
/// AOT rule (MVVMTK0045): [ObservableProperty] on PARTIAL PROPERTIES
/// only, matching <see cref="ShellViewModel"/>.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly PlayerService _player;
    private bool _disposed;

    public PlayerViewModel(PlayerService player)
    {
        _player = player;
        _player.StateChanged += OnPlayerStateChanged;
        _player.TrackFailed += OnTrackFailed;
        SyncFromPlayer();
    }

    /// <summary>The live queue (the service's own collection — ListView reorder writes through).</summary>
    public ObservableCollection<Song> Queue => _player.Queue;

    /// <summary>The engine, for the page's MediaPlayerElement.SetMediaPlayer (design §7.1).</summary>
    public MediaPlayer MediaPlayer => _player.Player;

    [ObservableProperty]
    public partial Song? CurrentSong { get; set; }

    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = "Nothing playing";

    [ObservableProperty]
    public partial string CurrentArtist { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "0:00";

    [ObservableProperty]
    public partial int CurrentQueueIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool IsShuffle { get; set; }

    [ObservableProperty]
    public partial bool IsSleepTimerActive { get; set; }

    [ObservableProperty]
    public partial string? PlaybackError { get; set; }

    [ObservableProperty]
    public partial bool HasPlaybackError { get; set; }

    /// <summary>Glyph for the primary transport button.</summary>
    public Symbol PlayPauseSymbol => IsPlaying ? Symbol.Pause : Symbol.Play;

    /// <summary>Glyph for the repeat button (All/One read literally; Off reuses All, dimmed by the page).</summary>
    public Symbol RepeatSymbol => _player.Repeat == RepeatMode.One ? Symbol.RepeatOne : Symbol.RepeatAll;

    public RepeatMode RepeatMode => _player.Repeat;

    /// <summary>Sleep-timer chip visibility.</summary>
    public Visibility SleepTimerVisibility =>
        IsSleepTimerActive ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsShuffleChanged(bool value) => _player.Shuffle = value;

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseSymbol));

    partial void OnIsSleepTimerActiveChanged(bool value) =>
        OnPropertyChanged(nameof(SleepTimerVisibility));

    // ------------------------------------------------------------------
    // Commands / transport forwards
    // ------------------------------------------------------------------

    [RelayCommand]
    private void TogglePlayPause() => _player.TogglePlayPause();

    [RelayCommand]
    private void NextTrack() => _player.Next();

    [RelayCommand]
    private void PreviousTrack() => _player.Previous();

    [RelayCommand]
    private void CycleRepeat()
    {
        _player.CycleRepeatMode();
        OnPropertyChanged(nameof(RepeatSymbol));
        OnPropertyChanged(nameof(RepeatMode));
    }

    [RelayCommand]
    private void DismissError()
    {
        PlaybackError = null;
        HasPlaybackError = false;
    }

    /// <summary>Seek-bar commit (the page guards drag state; this is the landing call).</summary>
    public void SeekTo(double seconds)
    {
        if (DurationSeconds <= 0)
        {
            return;
        }

        _player.Seek(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds)));
    }

    /// <summary>Queue tap-to-jump.</summary>
    public void PlayQueueItem(Song song)
    {
        int index = _player.Queue.IndexOf(song);
        if (index >= 0)
        {
            _player.PlayQueueIndex(index);
        }
    }

    /// <summary>Sleep-timer menu: null = off.</summary>
    public void SetSleepTimer(TimeSpan? duration) => _player.SetSleepTimer(duration);

    /// <summary>Speed menu (design §7.1 rates).</summary>
    public void SetPlaybackRate(double rate) => _player.SetPlaybackRate(rate);

    // ------------------------------------------------------------------
    // Sync
    // ------------------------------------------------------------------

    private void OnPlayerStateChanged(object? sender, EventArgs e) => SyncFromPlayer();

    private void OnTrackFailed(object? sender, string message)
    {
        PlaybackError = message;
        HasPlaybackError = true;
    }

    private void SyncFromPlayer()
    {
        Song? song = _player.CurrentSong;
        CurrentSong = song;
        CurrentTitle = song?.Name ?? "Nothing playing";
        CurrentArtist = song?.PrimaryArtistNames ?? string.Empty;
        IsPlaying = _player.IsPlaying;
        PositionSeconds = _player.Position.TotalSeconds;
        DurationSeconds = _player.Duration.TotalSeconds;
        PositionText = FormatTime(_player.Position);
        DurationText = FormatTime(_player.Duration);
        CurrentQueueIndex = _player.CurrentQueueIndex;
        if (IsShuffle != _player.Shuffle)
        {
            IsShuffle = _player.Shuffle;
        }

        IsSleepTimerActive = _player.IsSleepTimerActive;
        OnPropertyChanged(nameof(PlayPauseSymbol));
        OnPropertyChanged(nameof(RepeatSymbol));
        OnPropertyChanged(nameof(RepeatMode));
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.StateChanged -= OnPlayerStateChanged;
        _player.TrackFailed -= OnTrackFailed;
    }
}
