using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Search page (design §9.3). The app's only search field lives at
/// the top of this page (Manish, 2026-10-08 — the sidebar carries a
/// plain Search row); a query can also still arrive as a string
/// navigation parameter. Code-behind: the box's submit path, query
/// intake on navigation, tab panel switching, result navigation.
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

    private bool _topResultsHooked;
    private int _searchFocusRequest;

    /// <summary>
    /// The Top tab's rows as actually rendered: ViewModel.TopResults
    /// minus DisplayOnly items (channels, shows — kinds with no
    /// detail page and no playback). SearchViewModel already drops
    /// them when composing TopResults; this page-level mirror
    /// enforces the same rule at the rendering surface
    /// (DETAIL_PAGE_DESIGN §2: the Top tab must never render an
    /// inert row), so the guarantee survives any future composition
    /// change. The typed tabs bind their own collections and are
    /// unaffected.
    /// </summary>
    public ObservableCollection<SearchResultItemViewModel> FilteredTopResults { get; } = new();

    /// <summary>
    /// Query intake: a non-empty string parameter fills the box and
    /// runs the search on the Top tab. A null/other parameter leaves
    /// the page in its current (idle or previous results) state.
    /// Either way the box takes keyboard focus, so landing on Search
    /// means the user can type immediately.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        HookTopResults();
        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            SearchBox.Text = query;
            TabTop.IsSelected = true;
            SetTab(0);
            await ViewModel.SearchAsync(query);
        }

        FocusSearchBox();
    }

    /// <summary>
    /// The box's submit path — identical to the parameter intake:
    /// a non-empty query searches on the Top tab (submit-only;
    /// ChosenSuggestion is not used — there is no suggestion list).
    /// </summary>
    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string query = (args.QueryText ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return;
        }

        TabTop.IsSelected = true;
        SetTab(0);
        await ViewModel.SearchAsync(query);
    }

    /// <summary>
    /// Focuses the search box and selects any existing query so
    /// typing replaces it. Called on navigation, again by the shell
    /// after its post-navigation selection sync, and by Ctrl+F when
    /// this page is already current.
    ///
    /// The request is deliberately queued at Low priority and
    /// retried a bounded number of times. A normal-priority request
    /// from OnNavigatedTo can run before Frame navigation has
    /// finished settling; the shell then assigns NavSearchItem to
    /// NavigationView.SelectedItem, and the NavigationView moves
    /// keyboard focus to that selected item. Low priority runs
    /// behind that navigation/selection work, and the retry covers
    /// a Focus call made before the AutoSuggestBox template's inner
    /// TextBox is ready to accept it.
    /// </summary>
    public void FocusSearchBox()
    {
        int request = ++_searchFocusRequest;
        EnqueueSearchBoxFocus(request, attempt: 0);
    }

    private void EnqueueSearchBoxFocus(int request, int attempt)
    {
        DispatcherQueue.GetForCurrentThread().TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (request != _searchFocusRequest)
                {
                    return;
                }

                TextBox? textBox = FindDescendant<TextBox>(SearchBox);
                bool focusAccepted = SearchBox.Focus(FocusState.Keyboard);
                if (!focusAccepted && textBox is not null)
                {
                    focusAccepted = textBox.Focus(FocusState.Keyboard);
                }

                object? focusedElement = XamlRoot is null
                    ? null
                    : FocusManager.GetFocusedElement(XamlRoot);
                bool boxHasFocus = focusAccepted
                    || ReferenceEquals(focusedElement, SearchBox)
                    || (textBox is not null && ReferenceEquals(focusedElement, textBox));

                if (boxHasFocus)
                {
                    textBox?.SelectAll();
                    return;
                }

                if (attempt < 10)
                {
                    EnqueueSearchBoxFocus(request, attempt + 1);
                }
            });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // A queued focus request belongs to this arrival only; do
        // not let it pull focus back after the user has moved on.
        _searchFocusRequest++;
        UnhookTopResults();
        ViewModel.CancelLoads();
        base.OnNavigatedFrom(e);
    }

    private void HookTopResults()
    {
        if (_topResultsHooked)
        {
            return;
        }

        _topResultsHooked = true;
        ViewModel.TopResults.CollectionChanged += TopResults_CollectionChanged;
        RebuildFilteredTopResults();
    }

    private void UnhookTopResults()
    {
        if (!_topResultsHooked)
        {
            return;
        }

        _topResultsHooked = false;
        ViewModel.TopResults.CollectionChanged -= TopResults_CollectionChanged;
    }

    private void RebuildFilteredTopResults()
    {
        FilteredTopResults.Clear();
        foreach (SearchResultItemViewModel row in ViewModel.TopResults)
        {
            if (row.Kind != HomeEntityKind.DisplayOnly)
            {
                FilteredTopResults.Add(row);
            }
        }
    }

    private void TopResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                {
                    // The source index counts items this mirror
                    // drops; translate it into filtered coordinates
                    // (the count of kept rows before the insert
                    // point) so ordering matches the source exactly.
                    int index = ViewModel.TopResults
                        .Take(e.NewStartingIndex)
                        .Count(row => row.Kind != HomeEntityKind.DisplayOnly);
                    foreach (SearchResultItemViewModel row in e.NewItems)
                    {
                        if (row.Kind != HomeEntityKind.DisplayOnly)
                        {
                            FilteredTopResults.Insert(index, row);
                            index++;
                        }
                    }
                }

                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                {
                    foreach (SearchResultItemViewModel row in e.OldItems)
                    {
                        // A dropped DisplayOnly row simply isn't
                        // here; Remove is then a harmless no-op.
                        FilteredTopResults.Remove(row);
                    }
                }

                break;

            default:
                // Replace / Move / Reset (Clear): rebuild.
                RebuildFilteredTopResults();
                break;
        }
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
