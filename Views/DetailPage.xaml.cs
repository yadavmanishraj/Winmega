using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
}
