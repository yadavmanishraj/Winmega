using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Search page (design §9.3). Code-behind: suggestion debounce
/// (~300ms per design §5.1), tab panel switching, navigation.
/// </summary>
public sealed partial class SearchPage : Page
{
    private readonly ILibraryStore _store;
    private CancellationTokenSource? _debounceCts;

    public SearchPage()
    {
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<SearchViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();
        ViewModel.AddToPlaylistRequested += OnAddToPlaylistRequested;
    }

    public SearchViewModel ViewModel { get; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelLoads();
        _debounceCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async void OnAddToPlaylistRequested(Song song) =>
        await PlaylistPicker.ShowAsync(XamlRoot, _store, song);

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _debounceCts.Token);
            await ViewModel.LoadSuggestionsAsync(sender.Text);
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string query = args.ChosenSuggestion as string ?? args.QueryText;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        TabTop.IsSelected = true;
        SetTab(0);
        await ViewModel.SearchAsync(query);
    }

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
        if (e.ClickedItem is SearchResultItemViewModel item && item.DetailArgs is { } args)
        {
            Frame.Navigate(typeof(DetailPage), args);
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
