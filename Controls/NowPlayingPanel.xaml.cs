using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Services;
using Omega.ViewModels;
using Windows.Foundation;

namespace Omega.Controls;

/// <summary>
/// The Now Playing panel (approved design, 2026-10-07) — the full
/// player as a shell-docked pane. Feature-parity successor of the
/// retired Now Playing page: same transient <see cref="PlayerViewModel"/>
/// lifecycle (resolved per instance, disposed with it), same seek
/// drag guard, same repeat-glyph refresh, same speed/sleep menus.
/// (The queue and lyrics segments the panel also carried were
/// removed 2026-10-08 — the strip flyouts own those surfaces now.)
///
/// The shell owns the panel's WIDTH (open/close slide, resize clamp
/// + persistence, narrow-window rule); this control owns its content
/// and raises the shell-facing events below. Nothing here navigates:
/// artist taps surface as <see cref="ArtistSelected"/> and the shell
/// pushes the Detail page under the still-open panel.
/// </summary>
public sealed partial class NowPlayingPanel : UserControl
{
    private readonly ILibraryStore _store;
    private bool _isDraggingSeek;
    private bool _isRefreshingSeek;
    private bool _disposed;
    private bool _isFavorite;
    private string? _favoriteSongId;
    private uint? _gripPointerId;
    private double _gripStartX;
    private double _gripStartWidth;

    public NowPlayingPanel()
    {
        // Resolved before InitializeComponent so compiled x:Bind can
        // read them (same pattern as the pages and the shell).
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<PlayerViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();

        // Resize cursor over the grip strip. Border is sealed and
        // ProtectedCursor is only settable on the derived control
        // itself, so the cursor lives on the panel: WinUI resolves
        // the pointer cursor up the tree from the hit element, and
        // the grip sets none of its own.
        GripBorder.PointerEntered += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        GripBorder.PointerExited += (_, _) => ProtectedCursor = null;

        PanelTitleText.Text = Res.Get("NowPlayingTitle");
        IdleTitleText.Text = Res.Get("NowPlayingIdleTitle");
        IdleBodyText.Text = Res.Get("NowPlayingIdleBody");

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnPanelUnloaded;
        RefreshFromViewModel();
        BuildByline();
        _ = RefreshFavoriteAsync();
    }

    /// <summary>Player view model (bound via x:Bind; this panel's own transient instance).</summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>The user closed the panel (✕). The shell slides it out and disposes it.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>An artist byline link was tapped; the shell navigates the content frame.</summary>
    public event EventHandler<string>? ArtistSelected;

    /// <summary>A grip drag began — the shell stops any width animation and snaps to the current width.</summary>
    public event EventHandler? ResizeDragStarted;

    /// <summary>Grip drag: the width the user is asking for (the shell clamps and applies it).</summary>
    public event EventHandler<double>? ResizeRequested;

    /// <summary>Grip drag ended — the shell persists the chosen width.</summary>
    public event EventHandler? ResizeDragEnded;

    /// <summary>Grip double-click — the shell restores the default width and persists it.</summary>
    public event EventHandler? ResizeResetRequested;

    /// <summary>
    /// Tears the panel down: unsubscribes and disposes this
    /// instance's view model. Called by the shell after the close
    /// slide (and via Unloaded as a safety net); idempotent.
    /// </summary>
    public void DisposePanel()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e) => DisposePanel();

    /// <summary>Resolves a theme brush by key (the favourite heart's active tint).</summary>
    private Brush? GetThemeBrush(string key)
    {
        // Framework theme brushes resolve straight off the
        // application resources (the MainWindow pattern); app-defined
        // brushes live in the theme dictionaries.
        if (Application.Current.Resources.TryGetValue(key, out object? direct)
            && direct is Brush directBrush)
        {
            return directBrush;
        }

        string dictionaryKey = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dictionaryKey, out object? value)
            && value is ResourceDictionary dictionary
            && dictionary.TryGetValue(key, out object? brush)
            && brush is Brush result)
        {
            return result;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // View-model sync (the page's RefreshFromViewModel, extended)
    // ------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshFromViewModel();

        if (e.PropertyName == nameof(PlayerViewModel.CurrentSong))
        {
            BuildByline();
            _ = RefreshFavoriteAsync();
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

        // The VM rebuilds its artwork image only on track change, so
        // re-pointing the image here is free on the 500 ms ticks.
        ArtworkImage.Source = ViewModel.CurrentArtworkSource;
        ArtworkPlaceholder.Visibility = ViewModel.CurrentArtworkSource is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Idle swap (Manish, 2026-10-08): nothing current → the
        // whole player body gives way to the placeholder; a song
        // becoming current (or being cleared back to null) flips it
        // here, in the same sync as everything else. A paused but
        // loaded song is NOT idle — CurrentSong is non-null.
        bool idle = ViewModel.CurrentSong is null;
        PlayerContent.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        IdlePlaceholder.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NowPlayingPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Artwork = panel width minus the grip (8) and the hero
        // zone's horizontal insets (20 + 12 + 8), capped so a
        // stretched panel keeps a sane poster size.
        double side = Math.Clamp(e.NewSize.Width - 48, 0, 400);
        if (side > 0)
        {
            ArtworkHost.Width = side;
            ArtworkHost.Height = side;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------
    // Byline: per-artist links (DetailPage header pattern)
    // ------------------------------------------------------------------

    private void BuildByline()
    {
        BylinePanel.Children.Clear();
        Song? song = ViewModel.CurrentSong;
        IReadOnlyList<ArtistRef> artists = song?.Artists.Primary ?? Array.Empty<ArtistRef>();
        if (song is null || artists.Count == 0)
        {
            BylinePanel.Children.Add(new TextBlock
            {
                Text = song?.PrimaryArtistNames ?? string.Empty,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return;
        }

        for (int i = 0; i < artists.Count; i++)
        {
            ArtistRef artist = artists[i];
            if (i > 0)
            {
                BylinePanel.Children.Add(new TextBlock
                {
                    Text = ", ",
                    Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var link = new HyperlinkButton
            {
                Content = artist.Name,
                Tag = artist.Id,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            link.Click += ArtistLink_Click;
            BylinePanel.Children.Add(link);
        }
    }

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: string artistId } && artistId.Length > 0)
        {
            ArtistSelected?.Invoke(this, artistId);
        }
    }

    // ------------------------------------------------------------------
    // Favourite (the SongRow/store path: IsFavoriteAsync /
    // SetFavoriteAsync against ILibraryStore)
    // ------------------------------------------------------------------

    private async Task RefreshFavoriteAsync()
    {
        Song? song = ViewModel.CurrentSong;
        _favoriteSongId = song?.Id;
        _isFavorite = false;
        if (song is not null)
        {
            try
            {
                _isFavorite = await _store.IsFavoriteAsync(song.Id);
            }
            catch (Exception)
            {
                // Store not ready — the heart renders unfavourited.
                _isFavorite = false;
            }
        }

        // A track change may have landed while the lookup ran.
        if (!string.Equals(_favoriteSongId, ViewModel.CurrentSong?.Id, StringComparison.Ordinal))
        {
            return;
        }

        RefreshFavoriteVisual();
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        Song? song = ViewModel.CurrentSong;
        if (song is null)
        {
            return;
        }

        bool target = !_isFavorite;
        try
        {
            await _store.SetFavoriteAsync(song, target);
            _isFavorite = target;
            RefreshFavoriteVisual();
        }
        catch (Exception)
        {
            // Leave the rendered state unchanged on store failure.
        }
    }

    private void RefreshFavoriteVisual()
    {
        // Segoe Fluent Icons: Heart (EB51) / HeartFill (EB52) — the
        // SongItemViewModel pair.
        FavoriteIcon.Glyph = _isFavorite ? "\uEB52" : "\uEB51";
        FavoriteIcon.Foreground = _isFavorite
            ? GetThemeBrush("TransportActiveBrush")
            : null;
        string name = Res.Get(_isFavorite ? "RemoveFromFavorites" : "AddToFavorites");
        ToolTipService.SetToolTip(FavoriteButton, name);
        AutomationProperties.SetName(FavoriteButton, name);
        FavoriteButton.IsEnabled = ViewModel.CurrentSong is not null;
    }

    // ------------------------------------------------------------------
    // Seek slider: drag guard (the page's, verbatim)
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
    // Transport extras + failure surface (the page's)
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

    private void FailureBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        ViewModel.DismissErrorCommand.Execute(null);

    private void FailureNext_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissErrorCommand.Execute(null);
        ViewModel.NextTrackCommand.Execute(null);
    }

    // ------------------------------------------------------------------
    // Resize grip: drag = resize (the shell clamps + persists),
    // double-click = reset. Deltas come from pointer positions
    // relative to the window root; dragging the LEFT edge left
    // widens the panel.
    // ------------------------------------------------------------------

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_gripPointerId is not null)
        {
            return;
        }

        if (!GripBorder.CapturePointer(e.Pointer))
        {
            return;
        }

        _gripPointerId = e.Pointer.PointerId;
        _gripStartX = e.GetCurrentPoint(null).Position.X;
        _gripStartWidth = ActualWidth;
        ResizeDragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gripPointerId != e.Pointer.PointerId)
        {
            return;
        }

        double currentX = e.GetCurrentPoint(null).Position.X;
        double requested = _gripStartWidth + (_gripStartX - currentX);
        ResizeRequested?.Invoke(this, requested);
        e.Handled = true;
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gripPointerId != e.Pointer.PointerId)
        {
            return;
        }

        _gripPointerId = null;
        GripBorder.ReleasePointerCapture(e.Pointer);
        ResizeDragEnded?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Grip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResizeResetRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
