using System;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.ViewModels;
using Windows.System;

namespace Omega.Views;

/// <summary>
/// Detail page (DETAIL_PAGE_DESIGN) for Album / Playlist (incl.
/// charts) / Artist. The song section header differs per kind ("Top
/// songs" for artists), set here from the navigation args; the sticky
/// mini bar condenses the header once it scrolls out. Band cards and
/// byline artist links navigate to further Detail pages. Back is
/// owned by the shell strip — no in-page back button.
/// </summary>
public sealed partial class DetailPage : Page
{
    /// <summary>Scroll offset past which the header counts as gone (header ≈ 232 art + padding).</summary>
    private const double MiniBarThreshold = 280;

    private readonly ILibraryStore _store;

    public DetailPage()
    {
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<DetailViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();
        ViewModel.AddToPlaylistRequested += OnAddToPlaylistRequested;
    }

    public DetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is DetailNavigationArgs args)
        {
            SongsHeader.Text = args.Kind == "artist"
                ? Res.Get("TopSongsHeader")
                : Res.Get("SongsHeader");
            DetailMiniBar.Visibility = Visibility.Collapsed;
            DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
            await ViewModel.LoadAsync(args);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelLoads();
        base.OnNavigatedFrom(e);
    }

    private async void OnAddToPlaylistRequested(Song song) =>
        await PlaylistPicker.ShowAsync(XamlRoot, _store, song);

    private void DetailScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        DetailMiniBar.Visibility = DetailScroller.VerticalOffset > MiniBarThreshold
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SongRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case SongItemViewModel row:
                row.PlayCommand.Execute(null);
                break;
            case RankedSongRow ranked:
                ranked.Row.PlayCommand.Execute(null);
                break;
        }
    }

    /// <summary>Album byline: a HyperlinkButton per artist, its id in Tag.</summary>
    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: string artistId } &&
            !string.IsNullOrWhiteSpace(artistId))
        {
            Frame.Navigate(typeof(DetailPage), DetailNavigationArgs.Artist(artistId));
        }
    }

    private bool _aboutDialogOpen;

    /// <summary>
    /// The full artist bio in a dialog: every section (heading +
    /// complete, selectable body) plus the About facts line, scrolling
    /// inside the dialog — the page itself shows only the 4-line
    /// preview of the first section.
    /// </summary>
    private async void AboutMore_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutDialogOpen)
        {
            return;
        }

        _aboutDialogOpen = true;
        try
        {
            var sections = new StackPanel { Spacing = 16 };
            foreach (DetailBioSection section in ViewModel.BioSections)
            {
                var block = new StackPanel { Spacing = 4 };
                string? title = section.Title;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    });
                }

                block.Children.Add(new TextBlock
                {
                    Text = section.Text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
                sections.Children.Add(block);
            }

            if (ViewModel.AboutFacts.Count > 0)
            {
                sections.Children.Add(new TextBlock
                {
                    Text = string.Join("  ·  ", ViewModel.AboutFacts),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Opacity = 0.75,
                });
            }

            AutomationProperties.SetAutomationId(sections, "DetailAboutFullText");

            var scroll = new ScrollViewer
            {
                Content = sections,
                MaxHeight = 480,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Res.Format("AboutDialogTitleFormat", ViewModel.Title),
                Content = scroll,
                CloseButtonText = Res.Get("CloseText"),
                DefaultButton = ContentDialogButton.Close,
            };
            AutomationProperties.SetAutomationId(dialog, "DetailAboutDialog");
            await dialog.ShowAsync();
        }
        finally
        {
            _aboutDialogOpen = false;
        }
    }

    // ActivateBandCard reads the item from the card root's
    // DataContext — which ItemsRepeater, unlike ListView/GridView,
    // NEVER assigns: its x:Bind templates render straight from the
    // template engine's context while the element's DataContext
    // property stays null, so a DataContext-reading handler silently
    // no-ops (the Home card-tap defect, proven by pointer trace
    // 2026-10-07 — every band card tap on this page was dead by the
    // identical mechanism). SetElementItem closes that gap: every
    // band repeater's ElementPrepared assigns the item as the
    // element's DataContext (and re-assigns it when the element
    // recycles to a new item). Do not remove those wirings — band
    // card taps and Enter/Space activation depend on them. (The
    // Latest Release card is a ContentControl, not a repeater:
    // ContentControl DOES propagate its Content as the templated
    // root's DataContext, so it needs no wiring.)

    private void BandCard_Tapped(object sender, TappedRoutedEventArgs e) =>
        ActivateBandCard(sender);

    /// <summary>Enter/Space on a focused card root activates it, but
    /// only when the key event originates at the root itself (the
    /// HomePage Card_KeyDown convention).</summary>
    private void BandCard_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space))
        {
            return;
        }

        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        ActivateBandCard(sender);
        e.Handled = true;
    }

    private void ActivateBandCard(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is TileItemViewModel tile &&
            !string.IsNullOrWhiteSpace(tile.Id))
        {
            Frame.Navigate(typeof(DetailPage), tile.DetailArgs);
        }
    }

    /// <summary>
    /// ElementPrepared for the band repeaters — the item assignment
    /// is all they need for tap/keyboard activation (see
    /// SetElementItem).
    /// </summary>
    private void BandRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) =>
        SetElementItem(sender, args);

    /// <summary>
    /// Assigns the item a realized band card element represents as
    /// its DataContext. ItemsRepeater never does this itself (its
    /// templates bind through the x:Bind template context, so cards
    /// render correctly with a null DataContext) — but the Tapped /
    /// KeyDown activation handlers identify the tapped card by its
    /// DataContext, so without this every band card tap is a silent
    /// no-op. ElementPrepared fires again when an element recycles
    /// to a different index, keeping the assignment current.
    /// </summary>
    private static void SetElementItem(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement element
            && sender.ItemsSource is IList items
            && args.Index >= 0 && args.Index < items.Count)
        {
            element.DataContext = items[args.Index];
        }
    }
}
