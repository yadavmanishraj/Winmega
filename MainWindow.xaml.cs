using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<NavigationViewItem> _playlistItems = new();
    private bool _syncingSelection;
    private Type? _lastPageType;
    private double _volumeBeforeMute = 100;

    public MainWindow()
    {
        // Resolved once from the composition root (design §3.3), before
        // InitializeComponent so compiled x:Bind can read them.
        ViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        Player = ((App)Application.Current).Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();

        // Custom title bar (spec §1): the unified strip is the drag
        // region; ExtendsContentIntoTitleBar is set in code (it errors
        // in XAML) and the system caption buttons stay, transparent
        // over Mica. The strip reserves their width via RightInset
        // (see TitleBarStrip_Loaded).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarStrip);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // Default size 1400x900 DIP is applied in TitleBarStrip_Loaded:
        // AppWindow.Resize takes PHYSICAL pixels, so the DIP size must
        // be scaled by RasterizationScale (only valid once the tree is
        // live). Resizing unscaled here would halve the layout space
        // on a 200%-scaled display and push the LCD well offscreen.

        ViewModel.Playlists.CollectionChanged += OnPlaylistsChanged;
        Player.PropertyChanged += OnPlayerPropertyChanged;

        NavigateTo(typeof(HomePage), null);
        RebuildPlaylistItems();
        UpdateTransportState();
        UpdateNowPlayingName();
        UpdateVolumeIcon();
        _ = ViewModel.RefreshPlaylistsAsync();
    }

    /// <summary>Shell view model (sidebar state: playlists).</summary>
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
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    // ------------------------------------------------------------------
    // Navigation: sidebar selection -> Frame, Frame -> selection sync
    // ------------------------------------------------------------------

    private void ShellNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection || sender.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        // Static page switch — no reflection-based navigation (AOT rule).
        switch (tag)
        {
            case "home":
                NavigateTo(typeof(HomePage), null);
                break;
            case "settings":
                NavigateTo(typeof(SettingsPage), null);
                break;
            case "library":
                NavigateTo(typeof(LibraryPage), null);
                break;
            case "library:history":
                NavigateTo(typeof(LibraryPage), LibraryNavigationArgs.History());
                break;
            case "library:artists":
            case "library:albums":
            case "library:songs":
                // Interim target (spec §1): the real Artists/Albums/
                // Songs aggregation lands in WS3; until then these
                // rows land on the Favorites tab.
                NavigateTo(typeof(LibraryPage), LibraryNavigationArgs.Favorites());
                break;
            case "library:newplaylist":
                NavigateTo(typeof(LibraryPage), LibraryNavigationArgs.Playlists());
                break;
            default:
                if (tag.StartsWith("playlist:", StringComparison.Ordinal))
                {
                    NavigateTo(typeof(LibraryPage),
                        LibraryNavigationArgs.Playlists(tag["playlist:".Length..]));
                }

                break;
        }
    }

    private void NavigateTo(Type pageType, object? parameter)
    {
        if (parameter is null && ContentFrame.CurrentSourcePageType == pageType)
        {
            return;
        }

        // Destination switches are transition-suppressed (spec §1);
        // drill-ins (Detail) are pushed by the pages themselves and
        // keep their slide transition.
        ContentFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        BackButton.IsEnabled = ContentFrame.CanGoBack;

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

    private void SyncLibrarySelection(object? parameter)
    {
        if (parameter is not LibraryNavigationArgs args)
        {
            SetSelectedItem(NavLibraryItem);
            return;
        }

        if (args.Tab == "history")
        {
            SetSelectedItem(NavRecentItem);
        }
        else if (args.Tab == "playlists" && args.PlaylistId is string id)
        {
            NavigationViewItem? item = _playlistItems.Find(
                candidate => (candidate.Tag as string) == "playlist:" + id);
            if (item is not null)
            {
                SetSelectedItem(item);
            }
        }

        // favorites (the Artists/Albums/Songs rows) and the bare
        // playlists tab are ambiguous at this layer — the row the user
        // clicked keeps its selection.
    }

    private void SetSelectedItem(NavigationViewItem? item)
    {
        if (ReferenceEquals(ShellNav.SelectedItem, item))
        {
            return;
        }

        _syncingSelection = true;
        ShellNav.SelectedItem = item;
        _syncingSelection = false;
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
        args.Handled = true;
    }

    // ------------------------------------------------------------------
    // LCD well + transport state
    // ------------------------------------------------------------------

    private void NowPlayingOpen_Click(object sender, RoutedEventArgs e)
    {
        // The well expands to the full Now Playing page (design §9.5);
        // the strip's lyrics and queue buttons land there too in v1
        // (queue pane is WS3/stretch).
        if (ContentFrame.CurrentSourcePageType != typeof(NowPlayingPage))
        {
            ContentFrame.Navigate(typeof(NowPlayingPage));
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
            case nameof(PlayerViewModel.Volume):
                UpdateVolumeIcon();
                break;
        }
    }

    /// <summary>
    /// Shuffle/repeat on-state (spec §3): green glyph — the accent,
    /// spent on state only — plus the state spelled out in the
    /// accessible name, so state is never carried by colour alone.
    /// </summary>
    private void UpdateTransportState()
    {
        Brush? activeBrush = GetTransportActiveBrush();

        ShuffleIcon.Foreground = Player.IsShuffle ? activeBrush : null;
        AutomationProperties.SetName(ShuffleButton,
            Player.IsShuffle ? Res.Get("ShuffleOn") : Res.Get("ShuffleOff"));

        bool repeatOn = Player.RepeatMode != RepeatMode.Off;
        RepeatIcon.Foreground = repeatOn ? activeBrush : null;
        AutomationProperties.SetName(RepeatButton, Player.RepeatMode switch
        {
            RepeatMode.One => Res.Get("RepeatOne"),
            RepeatMode.All => Res.Get("RepeatAll"),
            _ => Res.Get("RepeatOff"),
        });
    }

    private Brush? GetTransportActiveBrush()
    {
        // The brush lives in App.xaml's theme dictionaries (spec §4:
        // theme resources only); pick the dictionary matching the
        // shell's actual theme so a runtime Light/Dark switch in
        // Settings recolours the glyph too.
        string dictionaryKey = RootGrid.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dictionaryKey, out object? value)
            && value is ResourceDictionary dictionary
            && dictionary.TryGetValue("TransportActiveBrush", out object? brush)
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
