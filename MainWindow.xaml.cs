using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Persistence;
using Omega.Services;
using Omega.ViewModels;
using Omega.Views;
using Windows.Graphics;

namespace Omega;

/// <summary>
/// Shell window (APPLE_LAYOUT_SPEC §1; supersedes design §9.1's
/// SelectorBar strip + floating bar): unified title-bar strip
/// (transport + LCD well) over a NavigationView sidebar, content in
/// <c>ContentFrame</c>. Transport and the LCD bind
/// <see cref="PlayerViewModel"/> — the same VM the Now Playing page
/// uses — while <see cref="ShellViewModel"/> carries sidebar state.
/// Code-behind does navigation/event wiring only — no business logic.
///
/// Navigation model (audit fix wave): sidebar destinations are
/// handled on <c>ItemInvoked</c>, not <c>SelectionChanged</c> — an
/// invoke fires even for the already-selected row, so clicking the
/// current section while on a pushed page (Detail, Now Playing)
/// returns to that section's root (AUDIT_1 M5). Section switches
/// REPLACE rather than push: the back stack is cleared on every
/// top-level switch, so Back means "back within a drill-in chain",
/// not "replay the whole session" (AUDIT_3 M1).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<NavigationViewItem> _playlistItems = new();
    private Type? _lastPageType;
    private object? _currentNavParameter;
    private string? _navError;
    private double _volumeBeforeMute = 100;

    public MainWindow()
    {
        // Resolved once from the composition root (design §3.3), before
        // InitializeComponent so compiled x:Bind can read them.
        ViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        Player = ((App)Application.Current).Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();

        // Custom title bar (spec §1): ONLY the background border is
        // the drag region — the strip's interactive content is a
        // sibling ABOVE it, never a descendant, so manipulations that
        // start on a control (the volume Slider above all) belong to
        // the control, not to a window drag (POINTER QA F3).
        // ExtendsContentIntoTitleBar is set in code (it errors in
        // XAML); the system caption buttons stay, transparent over
        // Mica, and the strip reserves their width via RightInset
        // (see TitleBarStrip_Loaded).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // Default size 1400x900 DIP is applied in TitleBarStrip_Loaded:
        // AppWindow.Resize takes PHYSICAL pixels, so the DIP size must
        // be scaled by RasterizationScale (only valid once the tree is
        // live). Resizing unscaled here would halve the layout space
        // on a 200%-scaled display and push the LCD well offscreen.

        ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;
        ViewModel.Playlists.CollectionChanged += OnPlaylistsChanged;
        ViewModel.PropertyChanged += OnShellPropertyChanged;
        Player.PropertyChanged += OnPlayerPropertyChanged;

        NavigateSection(typeof(HomePage), null);
        RebuildPlaylistItems();
        UpdateTransportState();
        UpdateNowPlayingName();
        UpdateArtworkPlaceholder();
        UpdateEndpointStatus();
        UpdateVolumeIcon();
        _ = ViewModel.RefreshPlaylistsAsync();
    }

    /// <summary>Shell view model (sidebar state: playlists, endpoint state).</summary>
    public ShellViewModel ViewModel { get; }

    /// <summary>Player view model (transport + LCD bindings).</summary>
    public PlayerViewModel Player { get; }

    // ------------------------------------------------------------------
    // Title bar
    // ------------------------------------------------------------------

    private bool _initialSizeApplied;

    private void TitleBarStrip_Loaded(object sender, RoutedEventArgs e)
    {
        // RightInset is physical pixels; the spacer is in DIPs.
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        // One-time default size: 1400x900 DIP (the Apple silhouette —
        // 260 pane + LCD strip — wants the width), converted to the
        // physical pixels AppWindow.Resize expects.
        if (!_initialSizeApplied)
        {
            _initialSizeApplied = true;
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(1400 * scale),
                (int)Math.Round(900 * scale)));
        }

        CaptionInsetSpacer.Width = AppWindow.TitleBar.RightInset / scale;

        // ActualTheme is only settled once the tree is live.
        UpdateTransportState();
        UpdateEndpointStatus();
    }

    /// <summary>
    /// Responsive fold-down (AUDIT_2 R-1/R-2): below ~1100 DIP the
    /// strip's fixed cost would crush the LCD well, so the volume
    /// slider folds away (the speaker button — a mute toggle —
    /// remains, and volume stays reachable on the Now Playing page's
    /// surface and via keyboard focus on the button). Below the
    /// NavigationView's own compact tier (1008) the pane toggle
    /// appears: PaneDisplayMode=Auto already collapses the pane to
    /// icons and then to an overlay, but without a toggle button the
    /// overlay tier would be unreachable.
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        VolumeSlider.Visibility = e.NewSize.Width < 1100
            ? Visibility.Collapsed
            : Visibility.Visible;
        ShellNav.IsPaneToggleButtonVisible = e.NewSize.Width < 1008;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    // ------------------------------------------------------------------
    // Navigation: ItemInvoked -> Frame (replace), Frame -> selection sync
    // ------------------------------------------------------------------

    private void ShellNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        NavigateByTag(tag);
    }

    /// <summary>
    /// The Settings row lives in the pane FOOTER, which the
    /// NavigationView template hosts in a plain content presenter,
    /// outside the menu list's invocation pipeline: real-pointer QA
    /// (POINTER QA F2) proved its clicks never selected it and never
    /// raised SelectionChanged/ItemInvoked, while every menu item —
    /// served by the identical handler — worked. This explicit route
    /// feeds the same navigation path; it is idempotent alongside
    /// ItemInvoked (a section switch to the current section is a
    /// no-op), so the row works whichever route a given framework
    /// build delivers.
    /// </summary>
    private void NavSettingsItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        SetSelectedItem(NavSettingsItem);
        NavigateByTag("settings");
    }

    private void NavigateByTag(string tag)
    {
        // Static page switch — no reflection-based navigation (AOT rule).
        switch (tag)
        {
            case "home":
                NavigateSection(typeof(HomePage), null);
                break;
            case "settings":
                NavigateSection(typeof(SettingsPage), null);
                break;
            case "library":
                NavigateSection(typeof(LibraryPage), null);
                break;
            case "library:history":
                NavigateSection(typeof(LibraryPage), LibraryNavigationArgs.History());
                break;
            case "library:artists":
                NavigateSection(typeof(LibraryPage), LibraryNavigationArgs.Artists());
                break;
            case "library:albums":
                NavigateSection(typeof(LibraryPage), LibraryNavigationArgs.Albums());
                break;
            case "library:songs":
                NavigateSection(typeof(LibraryPage), LibraryNavigationArgs.Songs());
                break;
            case "library:newplaylist":
                NavigateSection(typeof(LibraryPage), LibraryNavigationArgs.Playlists());
                break;
            default:
                if (tag.StartsWith("playlist:", StringComparison.Ordinal))
                {
                    NavigateSection(typeof(LibraryPage),
                        LibraryNavigationArgs.Playlists(tag["playlist:".Length..]));
                }

                break;
        }
    }

    /// <summary>
    /// Top-level section switch: REPLACE semantics. Navigating to the
    /// section we are already on (same page, same parameter) is a
    /// no-op; anything else navigates fresh and clears the back
    /// stack, so a drill-in chain never leaks across sections.
    /// </summary>
    private void NavigateSection(Type pageType, object? parameter)
    {
        bool samePage = ContentFrame.CurrentSourcePageType == pageType;
        bool sameParameter = Equals(_currentNavParameter, parameter);
        if (samePage && sameParameter)
        {
            return;
        }

        // Destination switches are transition-suppressed (spec §1);
        // drill-ins (Detail) are pushed by the pages themselves and
        // keep their slide transition.
        ContentFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());
        ContentFrame.BackStack.Clear();
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        _currentNavParameter = e.Parameter;

        // A successful navigation clears any navigation failure the
        // shell bar is showing (playback errors are the VM's own
        // state and are not touched here).
        if (_navError is not null)
        {
            _navError = null;
            UpdateFailureBar();
        }

        // Leaving Library: playlists may have been created, renamed or
        // deleted there — refresh the sidebar section.
        if (_lastPageType == typeof(LibraryPage) && e.SourcePageType != typeof(LibraryPage))
        {
            _ = ViewModel.RefreshPlaylistsAsync();
        }

        _lastPageType = e.SourcePageType;

        if (e.SourcePageType == typeof(HomePage))
        {
            SetSelectedItem(NavHomeItem);
        }
        else if (e.SourcePageType == typeof(SettingsPage))
        {
            SetSelectedItem(NavSettingsItem);
        }
        else if (e.SourcePageType == typeof(SearchPage))
        {
            // Search is a field, not a row — nothing stays selected.
            SetSelectedItem(null);
        }
        else if (e.SourcePageType == typeof(LibraryPage))
        {
            SyncLibrarySelection(e.Parameter);
        }

        // Detail / Now Playing are pushes over the shell: the sidebar
        // selection stays on the last top-level row (Apple's model).
    }

    private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        // A failed navigation used to leave the shell silently stuck
        // on the previous page (the Settings QA failure mode). Mark
        // it handled and surface it on the shell failure bar instead.
        e.Handled = true;
        Debug.WriteLine($"[Shell] Navigation to {e.SourcePageType} failed: {e.Exception}");
        _navError = Res.Get("NavigationFailedMessage");
        UpdateFailureBar();
    }

    private void SyncLibrarySelection(object? parameter)
    {
        if (parameter is not LibraryNavigationArgs args)
        {
            SetSelectedItem(NavLibraryItem);
            return;
        }

        switch (args.Tab)
        {
            case "history":
                SetSelectedItem(NavRecentItem);
                break;
            case "artists":
                SetSelectedItem(NavArtistsItem);
                break;
            case "albums":
                SetSelectedItem(NavAlbumsItem);
                break;
            case "songs":
                SetSelectedItem(NavSongsItem);
                break;
            case "playlists" when args.PlaylistId is string id:
                NavigationViewItem? item = _playlistItems.Find(
                    candidate => (candidate.Tag as string) == "playlist:" + id);
                if (item is not null)
                {
                    SetSelectedItem(item);
                }

                break;

            // favorites and the bare playlists tab are ambiguous at
            // this layer — the row the user clicked keeps its
            // selection.
        }
    }

    private void SetSelectedItem(NavigationViewItem? item)
    {
        if (!ReferenceEquals(ShellNav.SelectedItem, item))
        {
            ShellNav.SelectedItem = item;
        }
    }

    // ------------------------------------------------------------------
    // Sidebar playlists (dynamic NavigationView items)
    // ------------------------------------------------------------------

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildPlaylistItems();

    private void RebuildPlaylistItems()
    {
        foreach (NavigationViewItem item in _playlistItems)
        {
            ShellNav.MenuItems.Remove(item);
        }

        _playlistItems.Clear();

        // Dynamic items live between the Playlists header and the
        // New playlist item — i.e. immediately before the latter.
        int insertAt = ShellNav.MenuItems.IndexOf(NavNewPlaylistItem);
        if (insertAt < 0)
        {
            insertAt = ShellNav.MenuItems.Count;
        }

        foreach (LibraryPlaylist playlist in ViewModel.Playlists)
        {
            var item = new NavigationViewItem
            {
                Content = playlist.Name,
                Tag = "playlist:" + playlist.Id,
                Icon = new SymbolIcon(Symbol.List),
            };
            AutomationProperties.SetAutomationId(item, "NavPlaylist-" + playlist.Id);
            AutomationProperties.SetName(item, playlist.Name);
            ShellNav.MenuItems.Insert(insertAt++, item);
            _playlistItems.Add(item);
        }
    }

    // ------------------------------------------------------------------
    // Sidebar search (the app's one search box — spec §1/§5)
    // ------------------------------------------------------------------

    private void SidebarSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Submit-only: use the submitted query text (ChosenSuggestion
        // is not used — there is no suggestion list in v1).
        string query = (args.QueryText ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return;
        }

        ContentFrame.Navigate(typeof(SearchPage), query, new SuppressNavigationTransitionInfo());
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SidebarSearch.Focus(FocusState.Keyboard);

        // Select the existing query so typing REPLACES it (pointer QA
        // wart: Ctrl+F used to append, producing "Arijit singharijit").
        // The inner TextBox is a template part of the AutoSuggestBox —
        // walk the visual tree (no reflection; AOT-safe).
        if (FindDescendant<TextBox>(SidebarSearch) is { } textBox)
        {
            textBox.SelectAll();
        }

        args.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // LCD well + transport state
    // ------------------------------------------------------------------

    private void NowPlayingOpen_Click(object sender, RoutedEventArgs e) =>
        NavigateToNowPlaying(null);

    private void LyricsButton_Click(object sender, RoutedEventArgs e) =>
        NavigateToNowPlaying("lyrics");

    private void QueueButton_Click(object sender, RoutedEventArgs e) =>
        NavigateToNowPlaying("queue");

    private void OpenQueueMenuItem_Click(object sender, RoutedEventArgs e) =>
        NavigateToNowPlaying("queue");

    /// <summary>
    /// The well, the Lyrics button and the Queue button all land on
    /// the Now Playing page (design §9.5); the section parameter
    /// tells the page which section the user asked for — lyrics opens
    /// the lyrics panel, queue scrolls the queue into view — instead
    /// of three controls silently doing the identical thing
    /// (AUDIT_1 B3).
    /// </summary>
    private void NavigateToNowPlaying(string? section)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(NowPlayingPage))
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), section);
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsEndpointReachable))
        {
            UpdateEndpointStatus();
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsShuffle):
            case nameof(PlayerViewModel.RepeatMode):
            case nameof(PlayerViewModel.RepeatSymbol):
                UpdateTransportState();
                break;
            case nameof(PlayerViewModel.CurrentTitle):
            case nameof(PlayerViewModel.CurrentArtist):
                UpdateNowPlayingName();
                break;
            case nameof(PlayerViewModel.CurrentArtworkSource):
                UpdateArtworkPlaceholder();
                break;
            case nameof(PlayerViewModel.Volume):
                UpdateVolumeIcon();
                break;
            case nameof(PlayerViewModel.HasPlaybackError):
            case nameof(PlayerViewModel.PlaybackError):
                // The status dot tells the truth about the last
                // playback outcome (AUDIT_3 M2).
                ViewModel.IsEndpointReachable = !Player.HasPlaybackError;
                UpdateFailureBar();
                break;
        }
    }

    /// <summary>
    /// Shuffle/repeat on-state (spec §3 + AUDIT_2 A-4): filled disc +
    /// green glyph + a dot under the glyph, so state reads by shape
    /// as well as colour, and the state is spelled out in the
    /// accessible name. Off-state keeps the plain disc from the
    /// shared strip style, glyph at full primary brush — the thin
    /// naked glyphs that vanished against the strip (V-02) are gone.
    /// </summary>
    private void UpdateTransportState()
    {
        Brush? activeBrush = GetTransportActiveBrush();
        Brush? discBrush = GetThemeBrush("SubtleFillColorSecondaryBrush");

        ShuffleIcon.Foreground = Player.IsShuffle ? activeBrush : null;
        ShuffleDisc.Fill = Player.IsShuffle ? discBrush : null;
        ShuffleStateDot.Visibility = Player.IsShuffle ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ShuffleButton,
            Player.IsShuffle ? Res.Get("ShuffleOn") : Res.Get("ShuffleOff"));

        bool repeatOn = Player.RepeatMode != RepeatMode.Off;
        RepeatIcon.Foreground = repeatOn ? activeBrush : null;
        RepeatDisc.Fill = repeatOn ? discBrush : null;
        RepeatStateDot.Visibility = repeatOn ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(RepeatButton, Player.RepeatMode switch
        {
            RepeatMode.One => Res.Get("RepeatOne"),
            RepeatMode.All => Res.Get("RepeatAll"),
            _ => Res.Get("RepeatOff"),
        });
    }

    private Brush? GetTransportActiveBrush() => GetThemeBrush("TransportActiveBrush");

    private Brush? GetThemeBrush(string key)
    {
        // Framework theme brushes (SystemFillColor*, SubtleFillColor*)
        // resolve straight off the application resources, which apply
        // the active theme dictionary.
        if (Application.Current.Resources.TryGetValue(key, out object? direct)
            && direct is Brush directBrush)
        {
            return directBrush;
        }

        // App-defined brushes live in App.xaml's theme dictionaries
        // (spec §4: theme resources only); pick the dictionary
        // matching the shell's actual theme so a runtime Light/Dark
        // switch in Settings recolours the shell too.
        string dictionaryKey = RootGrid.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dictionaryKey, out object? value)
            && value is ResourceDictionary dictionary
            && dictionary.TryGetValue(key, out object? brush)
            && brush is Brush result)
        {
            return result;
        }

        return null;
    }

    /// <summary>The well's single UIA name summarises the track (spec §1).</summary>
    private void UpdateNowPlayingName()
    {
        string title = Player.CurrentTitle;
        string artist = Player.CurrentArtist;
        string name = artist.Length == 0
            ? title
            : Res.Format("NowPlayingFormat", title, artist);
        AutomationProperties.SetName(NowPlayingOpenButton, name);

        // Idle reads as ONE line ("Nothing playing") — no empty
        // artist row under it (AUDIT_2 V-01).
        LcdArtistText.Visibility = artist.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Idle LCD: a neutral artwork placeholder tile stands in for missing art (V-01).</summary>
    private void UpdateArtworkPlaceholder()
    {
        ArtworkPlaceholder.Visibility = Player.CurrentArtworkSource is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Failure bar + endpoint status (AUDIT_3 M2)
    // ------------------------------------------------------------------

    /// <summary>
    /// The bar shows whichever failure is current: the player's
    /// (raised by PlayerService via PlayerViewModel) or a navigation
    /// failure (held locally — it has no VM home). Playback errors
    /// dismiss through the VM's own command so the Now Playing
    /// page's surface stays in sync.
    /// </summary>
    private void UpdateFailureBar()
    {
        string? text = Player.HasPlaybackError ? Player.PlaybackError : _navError;
        FailureBarText.Text = text ?? string.Empty;
        FailureBar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FailureDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (Player.HasPlaybackError)
        {
            Player.DismissErrorCommand.Execute(null);
        }

        _navError = null;
        UpdateFailureBar();
    }

    /// <summary>
    /// The dot is NOT a live network probe (no probing infrastructure
    /// exists) and is no longer dressed up as one: it reports the
    /// last playback outcome through ShellViewModel state — green
    /// "Direct · JioSaavn" while tracks load, caution + "Connection
    /// problem" after a failure.
    /// </summary>
    private void UpdateEndpointStatus()
    {
        bool ok = ViewModel.IsEndpointReachable;
        StatusDot.Fill = GetThemeBrush(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        EndpointStatusText.Text = ViewModel.EndpointStatusText;
        ToolTipService.SetToolTip(EndpointStatusPanel, ViewModel.EndpointStatusText);
    }

    // ------------------------------------------------------------------
    // Volume cluster
    // ------------------------------------------------------------------

    private void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        // Speaker button = mute toggle (shell-local memory of the
        // pre-mute level; the level itself is PlayerViewModel.Volume).
        if (Player.Volume > 0)
        {
            _volumeBeforeMute = Player.Volume;
            Player.Volume = 0;
        }
        else
        {
            Player.Volume = _volumeBeforeMute;
        }
    }

    private void UpdateVolumeIcon()
    {
        VolumeIcon.Symbol = Player.Volume <= 0 ? Symbol.Mute : Symbol.Volume;
    }
}
