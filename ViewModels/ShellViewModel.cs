using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Omega.Core.Models;
using Omega.Core.Playback;

namespace Omega.ViewModels;

/// <summary>
/// Shell state for the floating Now Playing bar: a thin mirror over
/// the singleton <see cref="IPlaybackGateway"/> (PlayerService,
/// design §7). The gateway raises <c>StateChanged</c> on the UI
/// thread, so every sync here is a direct property copy. The
/// play/pause command forwards to the gateway — the same transport
/// method the SMTC media keys invoke.
///
/// AOT rule (design §3.4 / MVVMTK0045): [ObservableProperty] is used on
/// PARTIAL PROPERTIES only — the field form is a compile error under
/// CsWinRT/AOT and is banned in this codebase.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly IPlaybackGateway _playback;
    private string? _artworkSongId;

    public ShellViewModel(IPlaybackGateway playback)
    {
        _playback = playback;
        NowPlayingTitle = "Nothing playing";
        NowPlayingSubtitle = "Pick a song to start listening";
        _playback.StateChanged += OnPlaybackStateChanged;
        SyncFromPlayback();
    }

    [ObservableProperty]
    public partial string NowPlayingTitle { get; set; }

    [ObservableProperty]
    public partial string NowPlayingSubtitle { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial ImageSource? NowPlayingArtworkSource { get; set; }

    /// <summary>Glyph for the bar's round play/pause button.</summary>
    public Symbol PlayPauseSymbol => IsPlaying ? Symbol.Pause : Symbol.Play;

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseSymbol));

    private void OnPlaybackStateChanged(object? sender, EventArgs e) => SyncFromPlayback();

    private void SyncFromPlayback()
    {
        Song? song = _playback.CurrentSong;
        NowPlayingTitle = song?.Name ?? "Nothing playing";
        NowPlayingSubtitle = song?.PrimaryArtistNames ?? "Pick a song to start listening";
        IsPlaying = _playback.IsPlaying;
        OnPropertyChanged(nameof(PlayPauseSymbol));

        // Rebuild the artwork image only when the track changes —
        // SyncFromPlayback runs on every 500 ms position tick too.
        if (!string.Equals(_artworkSongId, song?.Id, StringComparison.Ordinal))
        {
            _artworkSongId = song?.Id;
            string? artwork = song?.Image.Best;
            NowPlayingArtworkSource = artwork is null ? null : new BitmapImage(new Uri(artwork));
        }
    }

    [RelayCommand]
    private void TogglePlayPause() => _playback.TogglePlayPause();
}
