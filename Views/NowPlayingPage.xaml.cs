using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Upstream;
using Omega.Services;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Full Now Playing page (design §9.5). Code-behind does what XAML
/// cannot say safely: binding the singleton player into the
/// MediaPlayerElement, the seek slider's drag-state guard (position
/// ticks must never fight the user's thumb, design §7.1), the repeat
/// glyph, and the artwork poster. All state lives in
/// <see cref="PlayerViewModel"/> / PlayerService.
/// </summary>
public sealed partial class NowPlayingPage : Page
{
    private readonly JioSaavnClient _client;
    private bool _isDraggingSeek;
    private bool _isRefreshingSeek;
    private string? _posterSongId;
    private string? _requestedSection;
    private CancellationTokenSource? _lyricsCts;
    private string? _lyricsSongId;
    private bool _lyricsLoaded;

    public NowPlayingPage()
    {
        // Resolved before InitializeComponent so compiled x:Bind can
        // read it (same pattern as the shell window).
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<PlayerViewModel>();
        _client = services.GetRequiredService<JioSaavnClient>();
        InitializeComponent();
    }

    /// <summary>Now Playing view model (bound via x:Bind).</summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>
    /// Shell section request: the strip's Lyrics and Queue buttons
    /// navigate here with "lyrics" / "queue" so each lands on the
    /// section it names instead of all three well controls doing the
    /// identical thing (AUDIT_1 B3). A plain well click passes null.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _requestedSection = e.Parameter as string;
    }

    private void NowPlayingPage_Loaded(object sender, RoutedEventArgs e)
    {
        // One player, two surfaces (design §7.1): the element renders
        // the singleton's poster/artwork; audio never restarts.
        PlayerElement.SetMediaPlayer(ViewModel.MediaPlayer);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromViewModel();

        if (_requestedSection == "queue")
        {
            QueueSection.StartBringIntoView();
        }
        else if (_requestedSection == "lyrics")
        {
            // Fires LyricsToggle_Checked, which opens the panel and
            // starts the fetch.
            LyricsToggle.IsChecked = true;
        }
    }

    private void NowPlayingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _lyricsCts?.Cancel();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshFromViewModel();

        // Track changed under an open lyrics panel: refetch for the
        // new song (the fetch itself no-ops when the panel is shut).
        if (e.PropertyName == nameof(PlayerViewModel.CurrentSong))
        {
            _ = RefreshLyricsAsync();
        }
    }

    private void RefreshFromViewModel()
    {
        if (!_isDraggingSeek)
        {
            _isRefreshingSeek = true;
            SeekSlider.Value = Math.Min(ViewModel.PositionSeconds, SeekSlider.Maximum);
            _isRefreshingSeek = false;
        }

        RepeatIcon.Symbol = ViewModel.RepeatSymbol;
        RepeatButton.Opacity = ViewModel.RepeatMode == RepeatMode.Off ? 0.55 : 1.0;
        ToolTipService.SetToolTip(RepeatButton, ViewModel.RepeatMode switch
        {
            RepeatMode.All => "Repeat: all",
            RepeatMode.One => "Repeat: one",
            _ => "Repeat: off",
        });

        Song? song = ViewModel.CurrentSong;
        if (!string.Equals(_posterSongId, song?.Id, StringComparison.Ordinal))
        {
            _posterSongId = song?.Id;
            string? artwork = song?.Image.Large ?? song?.Image.Medium;
            PlayerElement.PosterSource = artwork is null ? null : new BitmapImage(new Uri(artwork));
        }
    }

    // ------------------------------------------------------------------
    // Seek slider: drag guard (design §7.1)
    // ------------------------------------------------------------------

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        _isDraggingSeek = true;

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSeek = false;
        ViewModel.SeekTo(SeekSlider.Value);
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Keyboard / assistive adjustments arrive without a pointer
        // drag — commit them immediately. Drags commit on release;
        // programmatic refreshes are flagged and ignored.
        if (!_isDraggingSeek && !_isRefreshingSeek)
        {
            ViewModel.SeekTo(e.NewValue);
        }
    }

    // ------------------------------------------------------------------
    // Transport extras + queue + failure surface
    // ------------------------------------------------------------------

    private void RepeatButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CycleRepeatCommand.Execute(null);

    private void SpeedItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item
            && double.TryParse(item.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
        {
            ViewModel.SetPlaybackRate(rate);
        }
    }

    private void SleepItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item
            && int.TryParse(item.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
        {
            ViewModel.SetSleepTimer(minutes <= 0 ? null : TimeSpan.FromMinutes(minutes));
        }
    }

    private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Song song)
        {
            ViewModel.PlayQueueItem(song);
        }
    }

    private void FailureBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        ViewModel.DismissErrorCommand.Execute(null);

    private void FailureNext_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissErrorCommand.Execute(null);
        ViewModel.NextTrackCommand.Execute(null);
    }

    // ------------------------------------------------------------------
    // Lyrics (AUDIT_3 B1: Core's GetLyricsAsync had no app-layer
    // caller — the feature existed upstream and nowhere in the UI)
    // ------------------------------------------------------------------

    private void LyricsToggle_Checked(object sender, RoutedEventArgs e)
    {
        LyricsSection.Visibility = Visibility.Visible;
        _ = RefreshLyricsAsync();
    }

    private void LyricsToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        LyricsSection.Visibility = Visibility.Collapsed;
        _lyricsCts?.Cancel();
    }

    /// <summary>
    /// Fetches the current song's lyrics. Gated on
    /// <see cref="Song.HasLyrics"/> (the search payload's flag is the
    /// accurate one — see JioSaavnClient.GetLyricsAsync). Every exit
    /// leaves the panel saying what happened: no track, no lyrics for
    /// this track, loading, loaded, or load failed — never a blank.
    /// </summary>
    private async Task RefreshLyricsAsync()
    {
        if (LyricsSection.Visibility != Visibility.Visible)
        {
            return;
        }

        Song? song = ViewModel.CurrentSong;
        if (song is null)
        {
            _lyricsSongId = null;
            _lyricsLoaded = false;
            LyricsBodyText.Text = string.Empty;
            SetLyricsStatus("Nothing playing — lyrics appear here when a track with lyrics is playing.");
            return;
        }

        if (!song.HasLyrics)
        {
            _lyricsSongId = song.Id;
            _lyricsLoaded = false;
            LyricsBodyText.Text = string.Empty;
            SetLyricsStatus("Lyrics aren't available for this track.");
            return;
        }

        if (_lyricsLoaded && string.Equals(_lyricsSongId, song.Id, StringComparison.Ordinal))
        {
            return;
        }

        _lyricsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lyricsCts = cts;
        string songId = song.Id;
        _lyricsSongId = songId;
        _lyricsLoaded = false;
        LyricsBodyText.Text = string.Empty;
        SetLyricsStatus("Loading lyrics…");

        try
        {
            LyricsResult? result = await _client.GetLyricsAsync(songId, cts.Token);
            if (cts.IsCancellationRequested
                || !string.Equals(_lyricsSongId, songId, StringComparison.Ordinal))
            {
                return;
            }

            if (result is null)
            {
                SetLyricsStatus("Lyrics aren't available for this track.");
                return;
            }

            LyricsBodyText.Text = result.Copyright is null
                ? result.Lyrics
                : result.Lyrics + "\n\n" + result.Copyright;
            SetLyricsStatus(null);
            _lyricsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer fetch or the panel closed.
        }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested)
            {
                SetLyricsStatus("Lyrics couldn't be loaded. Check your connection and try again.");
            }
        }
    }

    private void SetLyricsStatus(string? status)
    {
        LyricsStatusText.Text = status ?? string.Empty;
        LyricsStatusText.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
