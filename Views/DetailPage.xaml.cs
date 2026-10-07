using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Detail page (design §9.5) for Album / Playlist / Artist. The song
/// section header differs per kind ("Top songs" for artists), set
/// here from the navigation args.
/// </summary>
public sealed partial class DetailPage : Page
{
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

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void Tile_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TileItemViewModel tile)
        {
            Frame.Navigate(typeof(DetailPage), tile.DetailArgs);
        }
    }
}
