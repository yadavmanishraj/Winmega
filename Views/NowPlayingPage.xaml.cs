using System;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Omega.Core.Models;
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
    private bool _isDraggingSeek;
    private bool _isRefreshingSeek;
    private string? _posterSongId;

    public NowPlayingPage()
    {
        // Resolved before InitializeComponent so compiled x:Bind can
        // read it (same pattern as the shell window).
        ViewModel = ((App)Application.Current).Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();
    }

    /// <summary>Now Playing view model (bound via x:Bind).</summary>
    public PlayerViewModel ViewModel { get; }

    private void NowPlayingPage_Loaded(object sender, RoutedEventArgs e)
    {
        // One player, two surfaces (design §7.1): the element renders
        // the singleton's poster/artwork; audio never restarts.
        PlayerElement.SetMediaPlayer(ViewModel.MediaPlayer);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromViewModel();
    }

    private void NowPlayingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshFromViewModel();

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
}
