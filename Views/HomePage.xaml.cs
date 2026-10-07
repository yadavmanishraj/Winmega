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
/// Home page (design §9.2). Code-behind does navigation + dialog
/// hosting only; all data and playback live in HomeViewModel.
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly ILibraryStore _store;

    public HomePage()
    {
        IServiceProvider services = ((App)Application.Current).Services;
        ViewModel = services.GetRequiredService<HomeViewModel>();
        _store = services.GetRequiredService<ILibraryStore>();
        InitializeComponent();
        ViewModel.AddToPlaylistRequested += OnAddToPlaylistRequested;
    }

    public HomeViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
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
}
