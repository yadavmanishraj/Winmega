using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Omega.Controls;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Upstream;
using Omega.Services;
using Omega.ViewModels;
using Omega.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace Omega;

/// <summary>
/// Shell window (APPLE_LAYOUT_SPEC §1; supersedes design §9.1's
/// SelectorBar strip + floating bar): unified title-bar strip
/// (transport + LCD well) over a NavigationView sidebar, content in
/// <c>ContentFrame</c>, and the Now Playing panel docked beside it.
/// Transport and the LCD bind <see cref="PlayerViewModel"/> — the
/// same VM family the Now Playing panel uses (its own transient
/// instance) — while <see cref="ShellViewModel"/> carries sidebar
/// state. Code-behind does navigation/event wiring only — no
/// business logic.
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
    private readonly JioSaavnClient _client;
    private readonly ObservableCollection<QueueFlyoutItem> _upNextItems = new();
    private readonly Dictionary<string, LyricsResult?> _lyricsCache = new();
    private Type? _lastPageType;
    private object? _currentNavParameter;
    private string? _navError;
    private double _volumeBeforeMute = 100;
    private InputNonClientPointerSource? _nonClientSource;
    private RectInt32[] _passthroughRects = Array.Empty<RectInt32>();
    private bool _queueFlyoutOpen;
    private bool _lyricsFlyoutOpen;
    private CancellationTokenSource? _lyricsFlyoutCts;
    private string? _lyricsFlyoutSongId;

    // Now Playing panel (docked pane): created fresh on every open,
    // disposed after the close slide. The shell owns its width —
    // the persisted user choice (_panelRequestedWidth), the
    // 288-520 clamp, the narrow-window full-width rule, and the
    // slide animation (_panelTargetWidth is the last width the
    // shell committed to, animated or direct).
    private const string PanelWidthKey = "NowPlayingPanelWidth";
    private const double PanelDefaultWidth = 344;
    private const double PanelMinWidth = 288;
    private const double PanelMaxWidth = 520;
    private NowPlayingPanel? _nowPlayingPanel;
    private Storyboard? _panelStoryboard;
    private double _panelRequestedWidth = PanelDefaultWidth;
    private double _panelTargetWidth;

    public MainWindow()
    {
        // Resolved once from the composition root (design §3.3), before
        // InitializeComponent so compiled x:Bind can read them.
        ViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        Player = ((App)Application.Current).Services.GetRequiredService<PlayerViewModel>();
        _client = ((App)Application.Current).Services.GetRequiredService<JioSaavnClient>();
        InitializeComponent();

        // Custom title bar (spec §1): ONLY the background border is
        // the drag region — the strip's interactive content is a
        // sibling ABOVE it, never a descendant. The sibling shape
        // alone did NOT stop caption drags from starting on controls
        // (round-2 D3: a volume-Slider drag still moved the window);
        // the non-client Passthrough regions registered below are
        // what actually yield manipulation to the controls.
        // ExtendsContentIntoTitleBar is set in code (it errors in
        // XAML); the system caption buttons stay, transparent over
        // Mica, and the strip reserves their width via RightInset
        // (see TitleBarStrip_Loaded).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // Non-client passthrough (POINTER QA F3 / round-2 D3): the
        // drag Border is a SIBLING of the strip content, so the
        // framework subtracts NO interactive rects from the caption
        // region — clicks still reach the controls, but a drag that
        // starts on one (the volume Slider above all) is claimed by
        // the caption as a window move before the control can
        // capture it. Registering the interactive clusters as
        // Passthrough regions makes the non-client source yield
        // those rects to client input, so the Slider manipulates
        // and only genuinely empty strip area drags the window.
        // Rects are window-relative device pixels, refreshed on
        // layout (see UpdatePassthroughRegions).
        _nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        TitleBarStrip.LayoutUpdated += TitleBarStrip_LayoutUpdated;

        // Default size 1400x900 DIP is applied in TitleBarStrip_Loaded:
        // AppWindow.Resize takes PHYSICAL pixels, so the DIP size must
        // be scaled by RasterizationScale (only valid once the tree is
        // live). Resizing unscaled here would halve the layout space
        // on a 200%-scaled display and push the LCD well offscreen.

        _panelRequestedWidth = ReadPanelWidth();

        ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;
        ViewModel.Playlists.CollectionChanged += OnPlaylistsChanged;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        Player.Queue.CollectionChanged += OnQueueCollectionChanged;
        InitFlyoutTexts();

        NavigateSection(typeof(HomePage), null);
        RebuildPlaylistItems();
        UpdateTransportState();
        UpdateNowPlayingName();
        UpdateArtworkPlaceholder();
        UpdateVolumeIcon();

        // Pane toggle (Manish, 2026-10-08): tooltip + accessible
        // name from resw, like the transport state names.
        string toggleLabel = Res.Get("ToggleNavigationPane");
        ToolTipService.SetToolTip(PaneToggleButton, toggleLabel);
        AutomationProperties.SetName(PaneToggleButton, toggleLabel);
        _ = ViewModel.RefreshPlaylistsAsync();
    }

    /// <summary>Shell view model (sidebar state: playlists, endpoint state).</summary>
    public ShellViewModel ViewModel { get; }

    /// <summary>Player view model (transport + LCD bindings).</summary>
    public PlayerViewModel Player { get; }

    /// <summary>
    /// The queue flyout's "Up next" rows — the live queue minus the
    /// current track, rebuilt by <c>SyncQueueFlyout</c> whenever the
    /// queue or the current index moves while the flyout is open.
    /// The collection instance never changes, so the flyout's list
    /// binds it once.
    /// </summary>
    public ObservableCollection<QueueFlyoutItem> UpNextItems => _upNextItems;

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
        // physical pixels AppWindow expects, then CLAMPED into the
        // display work area together with the position. The clamp
        // matters: Windows restores the previous placement, and QA
        // drag history (plus a work area narrowed by a side-docked
        // taskbar) left the window ~290px taller than the visible
        // area with its bottom — including the pane footer's
        // Settings row — BELOW the screen, where no real click can
        // ever land (POINTER QA round 2, live-traced).
        if (!_initialSizeApplied)
        {
            _initialSizeApplied = true;
            RectInt32 workArea = DisplayArea
                .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)
                .WorkArea;
            int width = Math.Min((int)Math.Round(1400 * scale), workArea.Width);
            int height = Math.Min((int)Math.Round(900 * scale), workArea.Height);
            PointInt32 position = AppWindow.Position;
            int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - width);
            int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);
            int x = Math.Clamp(position.X, workArea.X, maxX);
            int y = Math.Clamp(position.Y, workArea.Y, maxY);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        CaptionInsetSpacer.Width = AppWindow.TitleBar.RightInset / scale;

        // ActualTheme is only settled once the tree is live.
        UpdateTransportState();
        UpdatePassthroughRegions();
    }

    private void TitleBarStrip_LayoutUpdated(object? sender, object e) =>
        UpdatePassthroughRegions();

    /// <summary>
    /// Marks the strip's interactive clusters (pane toggle, back
    /// button, transport cluster, LCD well, right cluster incl. the volume
    /// Slider) as non-client Passthrough rects, so pointer
    /// manipulation that starts on a control belongs to the control
    /// instead of becoming a caption drag. Recomputed on every
    /// layout pass but only pushed to the source when a rect
    /// actually changed (window resizes shift the centred well;
    /// the volume fold-down removes the Slider's band).
    /// </summary>
    private void UpdatePassthroughRegions()
    {
        if (_nonClientSource is null)
        {
            return;
        }

        var rects = new List<RectInt32>(5);
        AddPassthroughRect(rects, PaneToggleButton);
        AddPassthroughRect(rects, BackButton);
        AddPassthroughRect(rects, TransportCluster);
        AddPassthroughRect(rects, LcdWell);
        AddPassthroughRect(rects, RightCluster);
        if (rects.Count == 0)
        {
            return;
        }

        bool changed = rects.Count != _passthroughRects.Length;
        for (int i = 0; !changed && i < rects.Count; i++)
        {
            changed = !rects[i].Equals(_passthroughRects[i]);
        }

        if (!changed)
        {
            return;
        }

        _passthroughRects = rects.ToArray();
        _nonClientSource.SetRegionRects(NonClientRegionKind.Passthrough, _passthroughRects);
    }

    private static void AddPassthroughRect(List<RectInt32> rects, FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        // TransformToVisual(null) yields client-area DIPs with the
        // window's root as origin; the non-client source wants
        // device pixels in the same window-relative frame, so scale
        // by the rasterization scale.
        Point origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        double scale = element.XamlRoot?.RasterizationScale ?? 1.0;
        rects.Add(new RectInt32(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            (int)Math.Round(element.ActualWidth * scale),
            (int)Math.Round(element.ActualHeight * scale)));
    }

    /// <summary>
    /// Responsive fold-down (AUDIT_2 R-1/R-2): below ~1100 DIP the
    /// strip's fixed cost would crush the LCD well, so the volume
    /// slider folds away (the speaker button — a mute toggle —
    /// remains, and volume stays reachable on the Now Playing
    /// panel's surface and via keyboard focus on the button). Below the
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

        // The docked panel's effective width depends on the window
        // width (narrow-window rule), so a resize re-applies it —
        // unless a slide is mid-flight, whose target the completion
        // handler has already committed from the same rule.
        if (_nowPlayingPanel is not null && _panelStoryboard is null)
        {
            _panelTargetWidth = EffectivePanelWidth();
            _nowPlayingPanel.Width = _panelTargetWidth;
        }
    }

    /// <summary>
    /// Esc closes the Now Playing panel — and only the panel. The
    /// handler sits on the root grid's bubble route and acts solely
    /// on events nobody else consumed: flyouts and dialogs live in
    /// popup trees (their Esc never reaches this route), and
    /// controls that use Esc (the search box) mark it handled
    /// first. Navigation and outside clicks never close the panel.
    /// </summary>
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !e.Handled && _nowPlayingPanel is not null)
        {
            CloseNowPlayingPanel();
            e.Handled = true;
        }
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
    /// The Settings row lives in the pane FOOTER, hosted outside the
    /// menu list. Live debugging (2026-10-07, traced build) proved
    /// its clicks DO fire Tapped and ItemInvoked once the window is
    /// on-screen — the actual crash was downstream: assigning the
    /// footer item to <c>ShellNav.SelectedItem</c> throws a stowed
    /// exception (0xC000027B), because the selection state machine
    /// only tracks MenuItems (and the built-in settings item), never
    /// PaneFooter content. So this route only NAVIGATES; selection
    /// for the Settings page is synced to "no row" in
    /// ContentFrame_Navigated, never to the footer item itself.
    /// </summary>
    private void NavSettingsItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        NavigateByTag("settings");
    }

    /// <summary>
    /// Title-bar pane toggle (Manish, 2026-10-08): flips the pane
    /// between expanded and icon-rail on demand. PaneDisplayMode
    /// stays Auto — the width-driven auto-collapse he voted to keep
    /// (the pane rails itself when the Now Playing panel docks and
    /// narrows the NavigationView) is untouched; IsPaneOpen is the
    /// same state the built-in toggle button would drive.
    /// </summary>
    private void PaneToggle_Click(object sender, RoutedEventArgs e) =>
        ShellNav.IsPaneOpen = !ShellNav.IsPaneOpen;

    private void NavigateByTag(string tag)
    {
        // Static page switch — no reflection-based navigation (AOT rule).
        switch (tag)
        {
            case "home":
                NavigateSection(typeof(HomePage), null);
                break;
            case "search":
                NavigateSection(typeof(SearchPage), null);
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
        // The back button exists only when there is somewhere to
        // go back to (Manish, 2026-10-08): visibility follows the
        // same CanGoBack state that drives IsEnabled.
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        BackButton.Visibility = ContentFrame.CanGoBack
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            // NEVER select the footer item itself: assigning a
            // PaneFooter NavigationViewItem to NavigationView.
            // SelectedItem throws (0xC000027B) — the selection model
            // only resolves MenuItems. Settings is a footer
            // destination, so no menu row stays selected while it is
            // shown (the same treatment Search gets below).
            SetSelectedItem(null);
        }
        else if (e.SourcePageType == typeof(SearchPage))
        {
            // Search is a real sidebar row now (the input lives on
            // the page itself).
            SetSelectedItem(NavSearchItem);
        }
        else if (e.SourcePageType == typeof(LibraryPage))
        {
            SyncLibrarySelection(e.Parameter);
        }

        if (e.Content is SearchPage searchPage)
        {
            // Search focus is requested again only AFTER the
            // selection sync above. SearchPage.OnNavigatedTo makes
            // its request before this handler runs; on the Ctrl+F
            // path, where NavSearchItem was not already selected,
            // assigning SelectedItem lets the NavigationView move
            // focus to the newly selected row and win that race.
            // The page queues this final request at Low priority,
            // so it lands after navigation has settled. The nav-
            // click path sees the same final request and behaves
            // exactly as before.
            searchPage.FocusSearchBox();
        }

        // Detail pages are pushes over the shell: the sidebar
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
    // Search (Manish, 2026-10-08): the sidebar carries a plain
    // Search section row; the AutoSuggestBox itself lives at the top
    // of SearchPage and focuses itself on navigation. Ctrl+F is the
    // same destination: land on the page with its box focused (and
    // any existing query selected, so typing replaces it).
    // ------------------------------------------------------------------

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ContentFrame.CurrentSourcePageType == typeof(SearchPage)
            && ContentFrame.Content is SearchPage searchPage)
        {
            // Already there: no navigation happens, so focus the
            // live page's box directly.
            searchPage.FocusSearchBox();
        }
        else
        {
            // The page focuses its own box in OnNavigatedTo.
            NavigateSection(typeof(SearchPage), null);
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

    /// <summary>
    /// The mini player is a TOGGLE (Manish, 2026-10-08): open (or
    /// mid-open) closes through the same slide-out + dispose path
    /// as the panel's ✕; closed (or mid-close) opens — a mid-close
    /// click revives the closing panel, which OpenNowPlayingPanel
    /// already handles by cancelling the close slide.
    /// </summary>
    private void NowPlayingOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingPanel is not null && _panelTargetWidth > 0)
        {
            CloseNowPlayingPanel();
        }
        else
        {
            OpenNowPlayingPanel();
        }
    }

    /// <summary>
    /// The LCD ••• menu's "Go to Now Playing" keeps its original
    /// open-only semantics (a labelled navigation command in a menu
    /// is not a toggle).
    /// </summary>
    private void GoToNowPlayingMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenNowPlayingPanel();

    // "Open queue" from the LCD menu opens the strip's Queue flyout —
    // the queue's home since the panel went player-only (the label
    // must land on a queue, not the player panel).
    private void OpenQueueMenuItem_Click(object sender, RoutedEventArgs e) =>
        QueueFlyout.ShowAt(QueueButton);

    // ------------------------------------------------------------------
    // Now Playing panel (approved design, 2026-10-07): a shell-level
    // DOCKED pane — real layout space in Row 2 beside the
    // NavigationView, never an overlay. Open: a fresh
    // NowPlayingPanel instance is created, added at width 0 and
    // slid to its effective width (~320 ms decelerate). Close (the
    // panel's own ✕ or Esc — nothing else): slid back to 0
    // (~240 ms accelerate) and then DISPOSED (removed from the
    // tree, subscriptions dropped), so every open starts from a
    // clean instance. It stays docked across all frame navigation.
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens the panel. When it is already open this is a no-op,
    /// except mid-close: the close slide is cancelled and the panel
    /// settles back at its open width instead of disposing a panel
    /// the user just asked for.
    /// </summary>
    private void OpenNowPlayingPanel()
    {
        if (_nowPlayingPanel is not null)
        {
            // Reopen racing a close slide: cancel the close and
            // settle back at the open width instead of disposing a
            // panel the user just asked for.
            if (_panelStoryboard is not null && _panelTargetWidth == 0)
            {
                StopPanelStoryboard();
                _panelTargetWidth = EffectivePanelWidth();
                _nowPlayingPanel.Width = _panelTargetWidth;
            }

            return;
        }

        var panel = new NowPlayingPanel();
        _nowPlayingPanel = panel;
        panel.CloseRequested += Panel_CloseRequested;
        panel.ArtistSelected += Panel_ArtistSelected;
        panel.ResizeDragStarted += Panel_ResizeDragStarted;
        panel.ResizeRequested += Panel_ResizeRequested;
        panel.ResizeDragEnded += Panel_ResizeDragEnded;
        panel.ResizeResetRequested += Panel_ResizeResetRequested;
        NowPlayingPanelHost.Children.Add(panel);

        panel.Width = 0;
        _panelTargetWidth = EffectivePanelWidth();
        AnimatePanelWidth(_panelTargetWidth, opening: true);
    }

    private void CloseNowPlayingPanel()
    {
        if (_nowPlayingPanel is null)
        {
            return;
        }

        _panelTargetWidth = 0;
        AnimatePanelWidth(0, opening: false);
    }

    /// <summary>
    /// The width the panel should occupy right now: the user's
    /// persisted choice, except when the content left beside it
    /// would drop below ~560 DIP — then the panel takes the full
    /// content width instead of crushing the page (still docked
    /// mechanics, just full-bleed).
    /// </summary>
    private double EffectivePanelWidth()
    {
        double available = RootGrid.ActualWidth;
        if (available > 0 && available - _panelRequestedWidth < 560)
        {
            return available;
        }

        return _panelRequestedWidth;
    }

    /// <summary>
    /// Slides the panel's width (a dependent animation — layout
    /// must reflow every frame, which is the point of a docked
    /// pane). Reduced-motion (system animations off) snaps instead.
    /// On completion the width is committed as a plain local value
    /// so later direct sets (drag, resize) start from solid ground;
    /// a completed CLOSE disposes the panel.
    /// </summary>
    private void AnimatePanelWidth(double to, bool opening)
    {
        if (_nowPlayingPanel is null)
        {
            return;
        }

        StopPanelStoryboard();

        if (!AnimationsEnabled())
        {
            _nowPlayingPanel.Width = to;
            if (!opening)
            {
                DisposeNowPlayingPanel();
            }

            return;
        }

        var animation = new DoubleAnimation
        {
            From = _nowPlayingPanel.Width,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(opening ? 320 : 240)),
            EasingFunction = opening
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, _nowPlayingPanel);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_panelStoryboard, storyboard))
            {
                return;
            }

            _panelStoryboard = null;
            if (_nowPlayingPanel is not null)
            {
                _nowPlayingPanel.Width = to;
            }

            if (!opening)
            {
                DisposeNowPlayingPanel();
            }
        };
        _panelStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopPanelStoryboard()
    {
        if (_panelStoryboard is not null)
        {
            _panelStoryboard.Stop();
            _panelStoryboard = null;
        }
    }

    /// <summary>
    /// Removes the panel from the tree and tears it down. After
    /// this, nothing of the panel survives — the next open builds
    /// a fresh instance (fresh scroll position, fresh VM).
    /// </summary>
    private void DisposeNowPlayingPanel()
    {
        if (_nowPlayingPanel is null)
        {
            return;
        }

        StopPanelStoryboard();
        NowPlayingPanel panel = _nowPlayingPanel;
        _nowPlayingPanel = null;
        panel.CloseRequested -= Panel_CloseRequested;
        panel.ArtistSelected -= Panel_ArtistSelected;
        panel.ResizeDragStarted -= Panel_ResizeDragStarted;
        panel.ResizeRequested -= Panel_ResizeRequested;
        panel.ResizeDragEnded -= Panel_ResizeDragEnded;
        panel.ResizeResetRequested -= Panel_ResizeResetRequested;
        NowPlayingPanelHost.Children.Remove(panel);
        panel.DisposePanel();
    }

    private void Panel_CloseRequested(object? sender, EventArgs e) =>
        CloseNowPlayingPanel();

    private void Panel_ArtistSelected(object? sender, string artistId)
    {
        // A drill-in push under the still-open panel (the same
        // navigation the pages perform); the panel is shell-level
        // and is not part of the frame's tree, so it stays docked.
        ContentFrame.Navigate(typeof(DetailPage), DetailNavigationArgs.Artist(artistId));
    }

    private void Panel_ResizeDragStarted(object? sender, EventArgs e)
    {
        // A grab mid-slide: stop the animation and snap to the
        // width it was heading for, so the drag starts from the
        // settled geometry.
        StopPanelStoryboard();
        if (_nowPlayingPanel is not null)
        {
            _nowPlayingPanel.Width = _panelTargetWidth;
        }
    }

    private void Panel_ResizeRequested(object? sender, double requested)
    {
        if (_nowPlayingPanel is null)
        {
            return;
        }

        // Direct set — no transition lag while dragging. The narrow
        // -window rule can pin the effective width (full-bleed)
        // even while the requested width keeps changing underneath.
        _panelRequestedWidth = Math.Clamp(requested, PanelMinWidth, PanelMaxWidth);
        _panelTargetWidth = EffectivePanelWidth();
        _nowPlayingPanel.Width = _panelTargetWidth;
    }

    private void Panel_ResizeDragEnded(object? sender, EventArgs e) =>
        WritePanelWidth(_panelRequestedWidth);

    private void Panel_ResizeResetRequested(object? sender, EventArgs e)
    {
        if (_nowPlayingPanel is null)
        {
            return;
        }

        _panelRequestedWidth = PanelDefaultWidth;
        _panelTargetWidth = EffectivePanelWidth();
        _nowPlayingPanel.Width = _panelTargetWidth;
        WritePanelWidth(_panelRequestedWidth);
    }

    private static bool AnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Panel width persistence — the SettingsViewModel LocalSettings
    // pattern (primitives, try/catch: unpackaged dev runs have no
    // LocalSettings and simply don't persist).
    private static double ReadPanelWidth()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(PanelWidthKey, out object? value)
                && value is double width)
            {
                return Math.Clamp(width, PanelMinWidth, PanelMaxWidth);
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return PanelDefaultWidth;
    }

    private static void WritePanelWidth(double width)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[PanelWidthKey] = width;
        }
        catch (Exception)
        {
            // Unpackaged dev runs have no LocalSettings — the width
            // simply doesn't persist there.
        }
    }

    // ------------------------------------------------------------------
    // Strip flyouts: queue + lyrics quick views (approved sample,
    // 2026-10-07). Both are light-dismiss Flyouts hung off their
    // strip buttons; opening one is an outside click for the other,
    // so they never stack. Presentation state is synced in code
    // (the shell's pattern for its dynamic surfaces); the lyrics
    // fetch uses the Core call behind a HasLyrics gate with the
    // app's status wording, plus a per-song cache so reopening is
    // instant.
    // ------------------------------------------------------------------

    private void InitFlyoutTexts()
    {
        QueueFlyoutHeader.Text = Res.Get("Queue");
        LyricsFlyoutHeader.Text = Res.Get("Lyrics");
        QueueNowPlayingLabel.Text = Res.Get("QueueNowPlayingLabel");
        QueueUpNextLabel.Text = Res.Get("QueueUpNextLabel");
        ClearQueueButton.Content = Res.Get("QueueClear");
        QueueFlyoutEmptyTitle.Text = Res.Get("QueueEmptyTitle");
        QueueFlyoutEmptyBody.Text = Res.Get("QueueEmptyBody");
        LyricsFlyoutRetry.Content = Res.Get("LyricsRetry");
    }

    private void QueueFlyout_Opening(object sender, object e)
    {
        _queueFlyoutOpen = true;
        SyncQueueFlyout();
    }

    private void QueueFlyout_Closed(object sender, object e) =>
        _queueFlyoutOpen = false;

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_queueFlyoutOpen)
        {
            SyncQueueFlyout();
        }
    }

    /// <summary>
    /// Rebuilds the flyout's queue surfaces from the live queue: the
    /// header count (whole queue), and the "Up next" rows — every
    /// queue slot except the current index, in queue order. Empty
    /// up-next swaps the list for the composed empty state; Clear is
    /// enabled exactly when there is something to clear.
    /// </summary>
    private void SyncQueueFlyout()
    {
        ObservableCollection<Song> queue = Player.Queue;
        int currentIndex = Player.CurrentQueueIndex;
        bool hasCurrent = Player.CurrentSong is not null;

        QueueFlyoutCount.Text = Res.Format("SongsCountFormat", queue.Count);
        QueueNowPlayingLabel.Visibility = hasCurrent ? Visibility.Visible : Visibility.Collapsed;
        QueueNowPlayingBlock.Visibility = hasCurrent ? Visibility.Visible : Visibility.Collapsed;

        _upNextItems.Clear();
        for (int i = 0; i < queue.Count; i++)
        {
            if (i != currentIndex)
            {
                _upNextItems.Add(new QueueFlyoutItem(queue[i]));
            }
        }

        bool hasUpNext = _upNextItems.Count > 0;
        QueueFlyoutList.Visibility = hasUpNext ? Visibility.Visible : Visibility.Collapsed;
        QueueUpNextLabel.Visibility = hasUpNext ? Visibility.Visible : Visibility.Collapsed;
        QueueFlyoutEmpty.Visibility = hasUpNext ? Visibility.Collapsed : Visibility.Visible;
        ClearQueueButton.IsEnabled = hasUpNext;
    }

    private void QueueFlyoutList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueFlyoutItem item)
        {
            // Tap-to-jump; the flyout stays open and re-syncs onto
            // the new current track (StateChanged follows the jump).
            Player.PlayQueueItem(item.Song);
        }
    }

    private void QueueRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row && FindDescendant<Button>(row) is { } remove)
        {
            remove.Visibility = Visibility.Visible;
        }
    }

    private void QueueRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row && FindDescendant<Button>(row) is { } remove)
        {
            remove.Visibility = Visibility.Collapsed;
        }
    }

    private void QueueRowRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QueueFlyoutItem item })
        {
            return;
        }

        // The service keeps every queue invariant (current-index and
        // play-order remap) in its CollectionChanged handler, so a
        // plain removal IS the remove-from-queue op. Removal is by
        // reference: duplicate copies of one song in the queue must
        // not evict each other.
        ObservableCollection<Song> queue = Player.Queue;
        for (int i = 0; i < queue.Count; i++)
        {
            if (ReferenceEquals(queue[i], item.Song))
            {
                queue.RemoveAt(i);
                return;
            }
        }

        int index = queue.IndexOf(item.Song);
        if (index >= 0)
        {
            queue.RemoveAt(index);
        }
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        // Remove everything except the currently playing entry,
        // back to front so each removal's index remap stays trivial.
        // With nothing playing, the whole queue goes.
        ObservableCollection<Song> queue = Player.Queue;
        Song? current = Player.CurrentSong;
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(queue[i], current))
            {
                queue.RemoveAt(i);
            }
        }
    }

    private void LyricsFlyout_Opening(object sender, object e)
    {
        _lyricsFlyoutOpen = true;
        _ = RefreshLyricsFlyoutAsync();
    }

    private void LyricsFlyout_Closed(object sender, object e)
    {
        _lyricsFlyoutOpen = false;
        _lyricsFlyoutCts?.Cancel();
    }

    private void LyricsFlyoutRetry_Click(object sender, RoutedEventArgs e) =>
        _ = RefreshLyricsFlyoutAsync();

    private enum LyricsFlyoutState
    {
        Content,
        Loading,
        Message,
        Error,
    }

    /// <summary>
    /// Fetches the current song's lyrics for the flyout. Gated on
    /// <see cref="Song.HasLyrics"/> (the search payload's flag is
    /// the accurate one). Results cache per song id — including
    /// the "upstream has none" answer — so reopening the flyout is
    /// instant; a track change while the flyout is open refetches
    /// (see OnPlayerPropertyChanged).
    /// </summary>
    private async Task RefreshLyricsFlyoutAsync()
    {
        Song? song = Player.CurrentSong;
        LyricsFlyoutMeta.Text = song is null
            ? string.Empty
            : song.PrimaryArtistNames.Length == 0
                ? song.Name
                : Res.Format("LyricsMetaFormat", song.Name, song.PrimaryArtistNames);

        if (song is null)
        {
            _lyricsFlyoutSongId = null;
            SetLyricsFlyoutState(LyricsFlyoutState.Message, Res.Get("LyricsNothingPlaying"));
            return;
        }

        if (!song.HasLyrics)
        {
            _lyricsFlyoutSongId = song.Id;
            SetLyricsFlyoutState(LyricsFlyoutState.Message, Res.Get("LyricsUnavailable"));
            return;
        }

        if (_lyricsCache.TryGetValue(song.Id, out LyricsResult? cached))
        {
            _lyricsFlyoutSongId = song.Id;
            if (cached is null)
            {
                SetLyricsFlyoutState(LyricsFlyoutState.Message, Res.Get("LyricsUnavailable"));
            }
            else
            {
                ShowLyricsFlyout(cached);
            }

            return;
        }

        _lyricsFlyoutCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lyricsFlyoutCts = cts;
        string songId = song.Id;
        _lyricsFlyoutSongId = songId;
        SetLyricsFlyoutState(LyricsFlyoutState.Loading, Res.Get("LyricsLoading"));

        try
        {
            LyricsResult? result = await _client.GetLyricsAsync(songId, cts.Token);
            if (cts.IsCancellationRequested
                || !string.Equals(_lyricsFlyoutSongId, songId, StringComparison.Ordinal))
            {
                return;
            }

            _lyricsCache[songId] = result;
            if (result is null)
            {
                SetLyricsFlyoutState(LyricsFlyoutState.Message, Res.Get("LyricsUnavailable"));
                return;
            }

            ShowLyricsFlyout(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer fetch or the flyout closed.
        }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested)
            {
                SetLyricsFlyoutState(LyricsFlyoutState.Error, Res.Get("LyricsLoadFailed"));
            }
        }
    }

    private void ShowLyricsFlyout(LyricsResult result)
    {
        LyricsFlyoutText.Text = result.Lyrics;
        LyricsFlyoutCopyright.Text = result.Copyright ?? string.Empty;
        SetLyricsFlyoutState(LyricsFlyoutState.Content, null);
    }

    /// <summary>
    /// One visible surface per state: the lyrics body, or the status
    /// stack (spinner only while loading, Retry only on failure) —
    /// the panel always says what happened, never a blank.
    /// </summary>
    private void SetLyricsFlyoutState(LyricsFlyoutState state, string? message)
    {
        bool hasLyrics = state == LyricsFlyoutState.Content;
        LyricsFlyoutText.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
        if (!hasLyrics)
        {
            LyricsFlyoutText.Text = string.Empty;
            LyricsFlyoutCopyright.Text = string.Empty;
        }

        LyricsFlyoutStatus.Visibility = hasLyrics ? Visibility.Collapsed : Visibility.Visible;
        bool loading = state == LyricsFlyoutState.Loading;
        LyricsFlyoutProgress.IsActive = loading;
        LyricsFlyoutProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LyricsFlyoutStatusText.Text = message ?? string.Empty;
        LyricsFlyoutRetry.Visibility = state == LyricsFlyoutState.Error
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            case nameof(PlayerViewModel.CurrentSong):
                // Track changed: an open queue flyout re-pins its
                // now-playing block; an open lyrics flyout refetches
                // for the new song.
                if (_queueFlyoutOpen)
                {
                    SyncQueueFlyout();
                }

                if (_lyricsFlyoutOpen)
                {
                    _ = RefreshLyricsFlyoutAsync();
                }

                break;
            case nameof(PlayerViewModel.CurrentQueueIndex):
                if (_queueFlyoutOpen)
                {
                    SyncQueueFlyout();
                }

                break;
            case nameof(PlayerViewModel.CurrentArtworkSource):
                UpdateArtworkPlaceholder();
                break;
            case nameof(PlayerViewModel.Volume):
                UpdateVolumeIcon();
                break;
            case nameof(PlayerViewModel.HasPlaybackError):
            case nameof(PlayerViewModel.PlaybackError):
                // Failure honesty lives in the failure bar (the
                // strip's endpoint dot was removed, Manish
                // 2026-10-08).
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

        // Off-state glyph = explicit primary brush, never null:
        // assigning null here left the shuffle/repeat glyphs
        // unpainted in the strip (rendered proof 2026-10-08 — the
        // two icons whose foreground came only from this method
        // were invisible while prev/next, explicit in XAML,
        // rendered).
        Brush? primaryBrush = GetThemeBrush("TextFillColorPrimaryBrush");

        ShuffleIcon.Foreground = Player.IsShuffle ? activeBrush : primaryBrush;
        ShuffleDisc.Fill = Player.IsShuffle ? discBrush : null;
        ShuffleStateDot.Visibility = Player.IsShuffle ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ShuffleButton,
            Player.IsShuffle ? Res.Get("ShuffleOn") : Res.Get("ShuffleOff"));

        bool repeatOn = Player.RepeatMode != RepeatMode.Off;
        RepeatIcon.Foreground = repeatOn ? activeBrush : primaryBrush;
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
        Visibility visibility = Player.CurrentArtworkSource is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = visibility;
        QueueNowArtworkPlaceholder.Visibility = visibility;
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
