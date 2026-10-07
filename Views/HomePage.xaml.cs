using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Home page (HOME_PAGE_DESIGN). Code-behind does navigation + dialog
/// hosting, plus the section-grid machinery the design calls for:
/// chevron paging, wheel-to-horizontal chaining, entity-card hover
/// play discs, and the responsive card-size tiers. All data and
/// playback live in HomeViewModel.
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly ILibraryStore _store;
    private readonly List<SectionScroller> _sections = new();
    private bool _sectionsWired;
    private int _tier = -1;

    public HomePage()
    {
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<HomeViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();
        ViewModel.AddToPlaylistRequested += OnAddToPlaylistRequested;
        Loaded += HomePage_Loaded;
        SizeChanged += HomePage_SizeChanged;
    }

    public HomeViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
        // Sections that were hidden at first layout realize their
        // panels as the data lands — re-apply the active tier so a
        // late-realized grid doesn't sit at the XAML default size.
        ApplyTier(TierForWidth(ActualWidth), force: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelLoads();
        base.OnNavigatedFrom(e);
    }

    private async void OnAddToPlaylistRequested(Song song) =>
        await PlaylistPicker.ShowAsync(XamlRoot, _store, song);

    private void EntityList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeItemViewModel item && item.DetailArgs is { } args)
        {
            Frame.Navigate(typeof(DetailPage), args);
        }
    }

    private void SongRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongItemViewModel row)
        {
            row.PlayCommand.Execute(null);
        }
    }

    private void HeroOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Hero?.DetailArgs is { } args)
        {
            Frame.Navigate(typeof(DetailPage), args);
        }
    }

    // ------------------------------------------------------------------
    // Section wiring (chevrons / wheel) + responsive card tiers
    // ------------------------------------------------------------------

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_sectionsWired)
        {
            _sectionsWired = true;
            _sections.Add(new SectionScroller(JumpBackInSection, JumpBackInGrid, JumpBackInScrollPrev, JumpBackInScrollNext));
            _sections.Add(new SectionScroller(TrendingSection, TrendingGrid, TrendingScrollPrev, TrendingScrollNext));
            _sections.Add(new SectionScroller(NewAlbumsSection, NewAlbumsGrid, NewAlbumsScrollPrev, NewAlbumsScrollNext));
            _sections.Add(new SectionScroller(ChartsSection, ChartsGrid, ChartsScrollPrev, ChartsScrollNext));
            _sections.Add(new SectionScroller(TopPlaylistsSection, TopPlaylistsGrid, TopPlaylistsScrollPrev, TopPlaylistsScrollNext));
            _sections.Add(new SectionScroller(DiscoverSection, DiscoverGrid, DiscoverScrollPrev, DiscoverScrollNext));
        }

        ApplyTier(TierForWidth(ActualWidth), force: true);
    }

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyTier(TierForWidth(e.NewSize.Width), force: false);

    /// <summary>Card-size tiers by content width (design §2): the
    /// artwork only steps down; the 2-row shape never changes.</summary>
    private static int TierForWidth(double width) => width >= 1200 ? 0 : width >= 1000 ? 1 : 2;

    private void ApplyTier(int tier, bool force)
    {
        if (!force && tier == _tier)
        {
            return;
        }

        _tier = tier;
        (double entityWidth, double entityHeight) = tier switch
        {
            0 => (216d, 268d),
            1 => (192d, 244d),
            _ => (168d, 220d),
        };
        (double songWidth, double songHeight) = tier == 2 ? (288d, 88d) : (320d, 88d);

        SetWrapSize(NewAlbumsGrid, entityWidth, entityHeight);
        SetWrapSize(ChartsGrid, entityWidth, entityHeight);
        SetWrapSize(TopPlaylistsGrid, entityWidth, entityHeight);
        SetWrapSize(DiscoverGrid, entityWidth, entityHeight);
        SetWrapSize(JumpBackInGrid, songWidth, songHeight);
        SetWrapSize(TrendingGrid, songWidth, songHeight);
    }

    private static void SetWrapSize(GridView grid, double width, double height)
    {
        // Only the wrap grid's cell size changes per tier: the card
        // templates size their artwork from the cell by layout
        // (artwork row stretches), so no template or resource has to
        // be re-applied — recycled containers just re-measure.
        if (grid.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.ItemWidth = width;
            panel.ItemHeight = height;
        }
    }

    // ------------------------------------------------------------------
    // Entity cards: hover-revealed play disc
    // ------------------------------------------------------------------

    /// <summary>
    /// Wires the hover reveal for an entity card's play disc, once per
    /// container (the Tag marks a wired container; its template tree —
    /// and the handlers hung on it — recycles with it). The disc hides
    /// at rest so cards stay calm, shows while the pointer is over the
    /// card or the disc itself has keyboard focus. If the disc can't
    /// be found in a not-yet-realized tree, the card keeps the
    /// template's default always-visible disc.
    /// </summary>
    private void EntityGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        if (args.ItemContainer is not GridViewItem container)
        {
            return;
        }

        if (container.Tag is not null)
        {
            return;
        }

        Button? playButton = FindDescendant<Button>(container, "HomeEntityPlayButton");
        if (playButton is null)
        {
            return;
        }

        var state = new CardHoverState();
        container.Tag = state;
        SetDiscVisible(playButton, false);

        container.PointerEntered += (_, _) =>
        {
            state.PointerOver = true;
            SetDiscVisible(playButton, true);
        };
        container.PointerExited += (_, _) =>
        {
            state.PointerOver = false;
            if (playButton.FocusState == FocusState.Unfocused)
            {
                SetDiscVisible(playButton, false);
            }
        };
        playButton.GotFocus += (_, _) => SetDiscVisible(playButton, true);
        playButton.LostFocus += (_, _) =>
        {
            if (!state.PointerOver)
            {
                SetDiscVisible(playButton, false);
            }
        };
    }

    private static void SetDiscVisible(Button button, bool visible)
    {
        button.Opacity = visible ? 1 : 0;
        button.IsHitTestVisible = visible;
    }

    private sealed class CardHoverState
    {
        public bool PointerOver;
    }

    // ------------------------------------------------------------------
    // Visual-tree helpers (no reflection; AOT-safe — the MainWindow
    // FindDescendant pattern)
    // ------------------------------------------------------------------

    private static T? FindFirstDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindFirstDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && AutomationProperties.GetAutomationId(match) == automationId)
            {
                return match;
            }

            if (FindDescendant<T>(child, automationId) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// One Home section's scroll behaviour (design §4): the header
    /// chevrons stay hidden until the section is hovered (or a chevron
    /// takes keyboard focus) and page the grid by 85% of its viewport;
    /// a vertical mouse wheel over the grid scrolls the section
    /// horizontally and is only marked handled while the section can
    /// still move — at the end the event bubbles on to the page
    /// scroller, so wheel scrolling chains instead of trapping.
    /// Horizontal wheels (trackpads, shift+wheel) are left to the
    /// grid's internal scroller, which handles them natively.
    /// </summary>
    private sealed class SectionScroller
    {
        private readonly GridView _grid;
        private readonly Button _scrollPrev;
        private readonly Button _scrollNext;
        private ScrollViewer? _scroller;
        private bool _pointerOver;
        private bool _focusWithin;

        public SectionScroller(FrameworkElement section, GridView grid, Button scrollPrev, Button scrollNext)
        {
            _grid = grid;
            _scrollPrev = scrollPrev;
            _scrollNext = scrollNext;

            SetChevronsVisible(false);
            section.PointerEntered += (_, _) =>
            {
                _pointerOver = true;
                SetChevronsVisible(true);
            };
            section.PointerExited += (_, _) =>
            {
                _pointerOver = false;
                if (!_focusWithin)
                {
                    SetChevronsVisible(false);
                }
            };
            scrollPrev.Click += (_, _) => PageBy(-1);
            scrollNext.Click += (_, _) => PageBy(1);
            scrollPrev.GotFocus += (_, _) => OnChevronFocus(true);
            scrollNext.GotFocus += (_, _) => OnChevronFocus(true);
            scrollPrev.LostFocus += (_, _) => OnChevronFocus(false);
            scrollNext.LostFocus += (_, _) => OnChevronFocus(false);
            grid.PointerWheelChanged += Grid_PointerWheelChanged;
        }

        private ScrollViewer? Scroller => _scroller ??= FindFirstDescendant<ScrollViewer>(_grid);

        private void OnChevronFocus(bool focused)
        {
            _focusWithin = focused;
            if (focused || _pointerOver)
            {
                SetChevronsVisible(true);
            }
            else
            {
                SetChevronsVisible(false);
            }
        }

        private void PageBy(int direction)
        {
            if (Scroller is not { } scroller)
            {
                return;
            }

            double target = scroller.HorizontalOffset + (direction * scroller.ViewportWidth * 0.85);
            scroller.ChangeView(Math.Clamp(target, 0, scroller.ScrollableWidth), null, null);
        }

        private void Grid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (Scroller is not { } scroller)
            {
                return;
            }

            PointerPoint point = e.GetCurrentPoint(_grid);
            if (point.Properties.IsHorizontalMouseWheel)
            {
                return;
            }

            int delta = point.Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            double target = Math.Clamp(scroller.HorizontalOffset - delta, 0, scroller.ScrollableWidth);
            if (Math.Abs(target - scroller.HorizontalOffset) < 0.5)
            {
                // The section can't move in this direction — leave the
                // event unhandled so the page scroller takes it.
                return;
            }

            scroller.ChangeView(target, null, null, disableAnimation: true);
            e.Handled = true;
        }

        private void SetChevronsVisible(bool visible)
        {
            _scrollPrev.Opacity = visible ? 1 : 0;
            _scrollPrev.IsHitTestVisible = visible;
            _scrollNext.Opacity = visible ? 1 : 0;
            _scrollNext.IsHitTestVisible = visible;
        }
    }
}
