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
using Windows.System;

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

    /// <summary>Side of the entity artwork's square frame at the
    /// active tier (cell width − the template's 16px inset):
    /// 180 / 160 / 140. Applied to every realized card by
    /// ApplyArtworkFrameSize; the XAML default is the tier-0 180.</summary>
    private double _entityArtSide = 180;

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

    // Card activation lives on the templates' root elements (the
    // sections are ItemsRepeaters — no ItemClick). These mirror the
    // old GridView ItemClick handlers exactly: entity/trending cards
    // navigate to Detail when the item is navigable; song cards play.
    // A tap that lands on a card's own play button runs only the
    // button's Command (Play) and never navigates.

    private void EntityCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsFromPlayButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            return;
        }

        ActivateEntityCard(sender);
    }

    private void SongCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsFromPlayButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            return;
        }

        ActivateSongCard(sender);
    }

    /// <summary>Enter/Space on a focused card root activates it, but
    /// only when the key event originates at the root itself — a key
    /// press on the card's play button belongs to the button.</summary>
    private void Card_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space))
        {
            return;
        }

        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: HomeItemViewModel })
        {
            ActivateEntityCard(sender);
        }
        else if (sender is FrameworkElement { DataContext: SongItemViewModel })
        {
            ActivateSongCard(sender);
        }

        e.Handled = true;
    }

    private void ActivateEntityCard(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is HomeItemViewModel item && item.DetailArgs is { } args)
        {
            Frame.Navigate(typeof(DetailPage), args);
        }
    }

    private void ActivateSongCard(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is SongItemViewModel row)
        {
            row.PlayCommand.Execute(null);
        }
    }

    /// <summary>True when the tap originated inside one of the card
    /// templates' play buttons (they carry their own Command).</summary>
    private static bool IsFromPlayButton(DependencyObject? source, DependencyObject? root)
    {
        for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button button)
            {
                string id = AutomationProperties.GetAutomationId(button);
                if (id is "HomeEntityPlayButton" or "SongArtworkPlayButton")
                {
                    return true;
                }
            }
        }

        return false;
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
            _sections.Add(new SectionScroller(JumpBackInSection, JumpBackInScroller, JumpBackInScrollPrev, JumpBackInScrollNext));
            _sections.Add(new SectionScroller(TrendingSection, TrendingScroller, TrendingScrollPrev, TrendingScrollNext));
            _sections.Add(new SectionScroller(NewAlbumsSection, NewAlbumsScroller, NewAlbumsScrollPrev, NewAlbumsScrollNext));
            _sections.Add(new SectionScroller(ChartsSection, ChartsScroller, ChartsScrollPrev, ChartsScrollNext));
            _sections.Add(new SectionScroller(TopPlaylistsSection, TopPlaylistsScroller, TopPlaylistsScrollPrev, TopPlaylistsScrollNext));
            _sections.Add(new SectionScroller(DiscoverSection, DiscoverScroller, DiscoverScrollPrev, DiscoverScrollNext));
        }

        ApplyTier(TierForWidth(ActualWidth), force: true);
    }

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyTier(TierForWidth(e.NewSize.Width), force: false);

    /// <summary>Card-size tiers by content width (design §2): the
    /// artwork only steps down; the row shapes never change
    /// (entity sections 2 rows, song sections 3 rows).</summary>
    private static int TierForWidth(double width) => width >= 880 ? 0 : width >= 700 ? 1 : 2;

    private void ApplyTier(int tier, bool force)
    {
        if (!force && tier == _tier)
        {
            return;
        }

        _tier = tier;
        (double entityWidth, double entityHeight) = tier switch
        {
            0 => (196d, 248d),
            1 => (176d, 228d),
            _ => (156d, 208d),
        };
        (double songWidth, double songHeight) = tier == 2 ? (288d, 88d) : (320d, 88d);
        _entityArtSide = entityWidth - 16;

        // Entity sections: 2 rows, band = 2 x cell + 16. Song
        // sections: 3 rows, band = 3 x 88 + 2 x 16 = 296 at every
        // tier (the song cell height never changes per tier).
        double entityBand = (2 * entityHeight) + 16;
        SetSectionSize(NewAlbumsScroller, NewAlbumsRepeater, entityWidth, entityHeight, entityBand);
        SetSectionSize(ChartsScroller, ChartsRepeater, entityWidth, entityHeight, entityBand);
        SetSectionSize(TopPlaylistsScroller, TopPlaylistsRepeater, entityWidth, entityHeight, entityBand);
        SetSectionSize(DiscoverScroller, DiscoverRepeater, entityWidth, entityHeight, entityBand);
        SetSectionSize(JumpBackInScroller, JumpBackInRepeater, songWidth, songHeight, 296);
        SetSectionSize(TrendingScroller, TrendingRepeater, songWidth, songHeight, 296);
        ApplyArtworkFrameSizes();
    }

    private static void SetSectionSize(ScrollViewer scroller, ItemsRepeater repeater, double width, double height, double bandHeight)
    {
        // The cell size lives on the layout (the templates lay their
        // content out from the cell), so no template or resource has
        // to be re-applied — recycled elements just re-measure.
        if (repeater.Layout is UniformGridLayout layout)
        {
            layout.MinItemWidth = width;
            layout.MinItemHeight = height;
        }

        // The band height is what makes the layout flow sideways:
        // the ScrollViewer pins the section to its rows of cells,
        // and items wrap into new columns past the viewport —
        // horizontal scrolling engages. (The GridView/ItemsWrapGrid
        // version of this page rendered columns streaming down
        // instead, with or without the pin — out14/out15 proofs.)
        scroller.Height = bandHeight;
    }

    /// <summary>
    /// Pins one entity card's artwork frame to the tier's exact
    /// square. The frame's Width/Height are set explicitly — never
    /// left to a star-sized row — so the frame cannot drift
    /// rectangular and crop the art (Manish, 2026-10-07).
    /// </summary>
    private void ApplyArtworkFrameSize(FrameworkElement cardRoot)
    {
        if (FindDescendant<Border>(cardRoot, "HomeEntityArtwork") is { } frame)
        {
            frame.Width = _entityArtSide;
            frame.Height = _entityArtSide;
        }
    }

    /// <summary>Re-pins the frames of every realized entity card
    /// (called when the tier changes; cards realized later are
    /// pinned by EntityRepeater_ElementPrepared).</summary>
    private void ApplyArtworkFrameSizes()
    {
        foreach (ItemsRepeater repeater in new[] { NewAlbumsRepeater, ChartsRepeater, TopPlaylistsRepeater, DiscoverRepeater })
        {
            int count = VisualTreeHelper.GetChildrenCount(repeater);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(repeater, i) is FrameworkElement element)
                {
                    ApplyArtworkFrameSize(element);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Entity cards: hover-revealed play disc
    // ------------------------------------------------------------------

    /// <summary>
    /// Wires the hover reveal for an entity card's play disc, once per
    /// realized element (the Tag marks a wired element; its template
    /// tree — and the handlers hung on it — recycles with it). The
    /// disc hides at rest so cards stay calm, shows while the pointer
    /// is over the card or the disc itself has keyboard focus. If the
    /// disc can't be found in a not-yet-realized tree, the card keeps
    /// the template's default always-visible disc.
    /// </summary>
    private void EntityRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        ApplyArtworkFrameSize(element);

        Button? playButton = FindDescendant<Button>(element, "HomeEntityPlayButton");
        if (playButton is null)
        {
            return;
        }

        // Every realization starts calm, wired or not: a recycled
        // element may carry hover state from its previous item.
        SetDiscVisible(playButton, false);

        if (element.Tag is CardHoverState state)
        {
            state.PointerOver = false;
            return;
        }

        state = new CardHoverState();
        element.Tag = state;

        element.PointerEntered += (_, _) =>
        {
            state.PointerOver = true;
            SetDiscVisible(playButton, true);
        };
        element.PointerExited += (_, _) =>
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

    private void EntityRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        if (FindDescendant<Button>(element, "HomeEntityPlayButton") is { } playButton)
        {
            SetDiscVisible(playButton, false);
        }

        if (element.Tag is CardHoverState state)
        {
            state.PointerOver = false;
        }
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
    /// takes keyboard focus) and page the section by 85% of its
    /// viewport; a vertical mouse wheel over the section scrolls it
    /// horizontally and is only marked handled while the section can
    /// still move — at the end the event bubbles on to the page
    /// scroller, so wheel scrolling chains instead of trapping. If
    /// the ScrollViewer's own wheel handling already moved it (the
    /// event arrives handled), this handler stays out of the way.
    /// Horizontal wheels (trackpads, shift+wheel) are left to the
    /// scroller, which handles them natively.
    /// </summary>
    private sealed class SectionScroller
    {
        private readonly ScrollViewer _scroller;
        private readonly Button _scrollPrev;
        private readonly Button _scrollNext;
        private bool _pointerOver;
        private bool _focusWithin;

        public SectionScroller(FrameworkElement section, ScrollViewer scroller, Button scrollPrev, Button scrollNext)
        {
            _scroller = scroller;
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
            scroller.PointerWheelChanged += Scroller_PointerWheelChanged;
        }

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
            double target = _scroller.HorizontalOffset + (direction * _scroller.ViewportWidth * 0.85);
            _scroller.ChangeView(Math.Clamp(target, 0, _scroller.ScrollableWidth), null, null);
        }

        private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            PointerPoint point = e.GetCurrentPoint(_scroller);
            if (point.Properties.IsHorizontalMouseWheel)
            {
                return;
            }

            int delta = point.Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            double target = Math.Clamp(_scroller.HorizontalOffset - delta, 0, _scroller.ScrollableWidth);
            if (Math.Abs(target - _scroller.HorizontalOffset) < 0.5)
            {
                // The section can't move in this direction — leave the
                // event unhandled so the page scroller takes it.
                return;
            }

            _scroller.ChangeView(target, null, null, disableAnimation: true);
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
