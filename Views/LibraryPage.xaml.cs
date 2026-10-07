using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Library page (design §9.4). Code-behind: tab panel switching and
/// the playlist/history dialogs — create/rename validate a non-empty
/// name inline (error text under the field, dialog stays open);
/// delete/clear are verb-labelled confirmations. Shell navigation can
/// preselect a tab (and a playlist) via <see cref="LibraryNavigationArgs"/>.
/// </summary>
public sealed partial class LibraryPage : Page
{
    private readonly ILibraryStore _store;

    public LibraryPage()
    {
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<LibraryViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();
        ViewModel.AddToPlaylistRequested += OnAddToPlaylistRequested;
    }

    public LibraryViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.AttachDownloadUpdates();
        await ViewModel.LoadAllAsync();
        if (e.Parameter is LibraryNavigationArgs args)
        {
            ApplyNavigationArgs(args);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.DetachDownloadUpdates();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// One-shot application of shell navigation arguments: select the
    /// requested tab (SelectorBar visual state + panel via the same
    /// <see cref="SetTab"/> path the SelectionChanged handler uses) and,
    /// for the Playlists tab, preselect the requested playlist — setting
    /// <see cref="LibraryViewModel.SelectedPlaylist"/> loads its songs
    /// through the VM's selection handler. Runs only on navigation, so
    /// later user tab clicks are never overridden. Tab order (FX3):
    /// 0 Favorites, 1 Playlists, 2 Artists, 3 Albums, 4 Songs,
    /// 5 History, 6 Downloads.
    /// </summary>
    private void ApplyNavigationArgs(LibraryNavigationArgs args)
    {
        int index = args.Tab switch
        {
            "playlists" => 1,
            "artists" => 2,
            "albums" => 3,
            "songs" => 4,
            "history" => 5,
            "downloads" => 6,
            _ => 0,
        };

        SelectorBarItem item = index switch
        {
            1 => TabPlaylists,
            2 => TabArtists,
            3 => TabAlbums,
            4 => TabSongs,
            5 => TabHistory,
            6 => TabDownloads,
            _ => TabFavorites,
        };
        item.IsSelected = true;
        SetTab(index);

        if (args.Tab == "playlists" && args.PlaylistId is { } playlistId)
        {
            PlaylistItemViewModel? playlist =
                ViewModel.Playlists.FirstOrDefault(p => p.Id == playlistId);
            if (playlist is not null)
            {
                ViewModel.SelectedPlaylist = playlist;
            }
        }
    }

    private async void OnAddToPlaylistRequested(Song song) =>
        await PlaylistPicker.ShowAsync(XamlRoot, _store, song);

    private void LibraryTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem == TabPlaylists)
        {
            SetTab(1);
        }
        else if (sender.SelectedItem == TabArtists)
        {
            SetTab(2);
        }
        else if (sender.SelectedItem == TabAlbums)
        {
            SetTab(3);
        }
        else if (sender.SelectedItem == TabSongs)
        {
            SetTab(4);
        }
        else if (sender.SelectedItem == TabHistory)
        {
            SetTab(5);
        }
        else if (sender.SelectedItem == TabDownloads)
        {
            SetTab(6);
        }
        else
        {
            SetTab(0);
        }
    }

    private void SetTab(int index)
    {
        FavoritesPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AlbumsPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        SongsPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPanel.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Row activation: clicking a song row plays it (the row's Play command).</summary>
    private void SongRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongItemViewModel row)
        {
            row.PlayCommand.Execute(null);
        }
    }

    /// <summary>
    /// Group tile activation: groups carrying an upstream id open the
    /// matching Detail page; id-less (local-only) groups filter the
    /// Songs tab instead.
    /// </summary>
    private async void Group_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibraryGroupItemViewModel group)
        {
            return;
        }

        if (group.DetailArgs is { } args)
        {
            Frame.Navigate(typeof(DetailPage), args);
            return;
        }

        await ViewModel.ApplyGroupFilterAsync(group);
        TabSongs.IsSelected = true;
        SetTab(4);
    }

    /// <summary>Downloads row activation: play (completed) or retry (failed).</summary>
    private async void DownloadsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DownloadItemViewModel item)
        {
            await ViewModel.PlayDownloadAsync(item);
        }
    }

    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        string? name = await ShowPlaylistNameDialogAsync(Res.Get("NewPlaylistTitle"), string.Empty);
        if (name is not null)
        {
            await ViewModel.CreatePlaylistAsync(name);
        }
    }

    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPlaylist is null)
        {
            return;
        }

        string? name = await ShowPlaylistNameDialogAsync(
            Res.Get("RenamePlaylistTitle"), ViewModel.SelectedPlaylist.Name);
        if (name is not null)
        {
            await ViewModel.RenameSelectedPlaylistAsync(name);
        }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPlaylist is null)
        {
            return;
        }

        bool confirmed = await ConfirmAsync(
            Res.Get("DeletePlaylistTitle"),
            Res.Format("DeletePlaylistFormat", ViewModel.SelectedPlaylist.Name),
            Res.Get("Delete"));
        if (confirmed)
        {
            await ViewModel.DeleteSelectedPlaylistAsync();
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await ConfirmAsync(
            Res.Get("ClearHistoryTitle"),
            Res.Get("ClearHistoryMessage"),
            Res.Get("Clear"));
        if (confirmed)
        {
            await ViewModel.ClearHistoryAsync();
        }
    }

    /// <summary>
    /// Name-entry dialog shared by create/rename: a labelled TextBox
    /// (visible label, never placeholder-only) with the validation
    /// error under the field; blank names keep the dialog open.
    /// Returns the trimmed name, or null when cancelled.
    /// </summary>
    private async Task<string?> ShowPlaylistNameDialogAsync(string title, string initial)
    {
        var nameBox = new TextBox
        {
            Header = Res.Get("PlaylistNameLabel"),
            Text = initial,
        };
        AutomationProperties.SetAutomationId(nameBox, "PlaylistNameBox");

        var errorText = new TextBlock
        {
            Text = Res.Get("PlaylistNameRequired"),
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Spacing = 8, MinWidth = 320 };
        content.Children.Add(nameBox);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = Res.Get("Save"),
            CloseButtonText = Res.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "PlaylistNameDialog");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                args.Cancel = true;
                errorText.Visibility = Visibility.Visible;
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? nameBox.Text.Trim()
            : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = Res.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ConfirmDialog");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
