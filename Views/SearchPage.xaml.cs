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
/// Search page (design §9.3). The only search field in the app is the
/// shell's sidebar box (Apple layout); it navigates here with the query
/// as a string parameter. Code-behind: query intake on navigation, tab
/// panel switching, result navigation.
/// </summary>
public sealed partial class SearchPage : Page
{
    private readonly ILibraryStore _store;

    public SearchPage()
    {
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<SearchViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();
        ViewModel.AddToPlaylistRequested += OnAddToPlaylistRequested;
    }

    public SearchViewModel ViewModel { get; }

    /// <summary>
    /// Sidebar-search handoff: a non-empty string parameter runs the
    /// search on the Top tab (the exact path the page's own submit
    /// handler used before the box moved to the shell). A null/other
    /// parameter leaves the page in its current (idle or previous
    /// results) state.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            TabTop.IsSelected = true;
            SetTab(0);
            await ViewModel.SearchAsync(query);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelLoads();
        base.OnNavigatedFrom(e);
    }

    private async void OnAddToPlaylistRequested(Song song) =>
        await PlaylistPicker.ShowAsync(XamlRoot, _store, song);

    private void SearchTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem == TabSongs)
        {
            SetTab(1);
        }
        else if (sender.SelectedItem == TabAlbums)
        {
            SetTab(2);
        }
        else if (sender.SelectedItem == TabArtists)
        {
            SetTab(3);
        }
        else if (sender.SelectedItem == TabPlaylists)
        {
            SetTab(4);
        }
        else
        {
            SetTab(0);
        }
    }

    private void SetTab(int index)
    {
        TopPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SongsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AlbumsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TopResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItemViewModel item)
        {
            return;
        }

        if (item.DetailArgs is { } args)
        {
            Frame.Navigate(typeof(DetailPage), args);
        }
        else if (item.IsSong)
        {
            // Songs have no detail page (audit M6): a click on the
            // row plays it, exactly like the row's play button —
            // previously the click was a silent no-op.
            _ = item.PlayCommand.ExecuteAsync(null);
        }
    }

    private void SongRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongItemViewModel row)
        {
            row.PlayCommand.Execute(null);
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
