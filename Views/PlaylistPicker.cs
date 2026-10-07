using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Omega.Core.Models;
using Omega.Core.Persistence;

namespace Omega.Views;

/// <summary>
/// The shared "Add to playlist" picker (design §9.4 dialog idiom):
/// lists the user's playlists from <see cref="ILibraryStore"/>,
/// appends the song to the chosen one, and offers inline creation
/// with the same non-empty-name validation as Library. Built in code
/// because the hosting pages each raise it from a row command; list
/// items are plain wrapper records displayed via <c>ToString()</c> —
/// no reflection-based binding, keeping it trim/AOT-safe.
/// </summary>
public static class PlaylistPicker
{
    private sealed record PlaylistChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public static async Task ShowAsync(XamlRoot xamlRoot, ILibraryStore store, Song song)
    {
        await store.InitializeAsync();
        IReadOnlyList<LibraryPlaylist> playlists = await store.GetPlaylistsAsync();

        var choices = new List<PlaylistChoice>();
        foreach (LibraryPlaylist playlist in playlists)
        {
            choices.Add(new PlaylistChoice(playlist.Id, playlist.Name));
        }

        var list = new ListView
        {
            ItemsSource = choices,
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 280,
        };
        AutomationProperties.SetAutomationId(list, "PlaylistPickerList");

        var nameBox = new TextBox
        {
            Header = Res.Get("PlaylistNameLabel"),
            PlaceholderText = Res.Get("NewPlaylistPlaceholder"),
        };
        AutomationProperties.SetAutomationId(nameBox, "PlaylistPickerNameBox");

        // Validation is signalled by the text itself appearing under
        // the field (design §10: never colour-only signalling).
        var nameError = new TextBlock
        {
            Text = Res.Get("PlaylistNameRequired"),
            Visibility = Visibility.Collapsed,
        };

        var createButton = new Button
        {
            Content = Res.Get("CreatePlaylist"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(createButton, "PlaylistPickerCreate");

        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(list);
        content.Children.Add(nameBox);
        content.Children.Add(nameError);
        content.Children.Add(createButton);
        if (choices.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = Res.Get("PlaylistPickerEmpty"),
                TextWrapping = TextWrapping.Wrap,
            };
            content.Children.Insert(0, empty);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Res.Get("AddToPlaylistTitle"),
            CloseButtonText = Res.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = content,
        };
        AutomationProperties.SetAutomationId(dialog, "PlaylistPickerDialog");

        list.ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is PlaylistChoice choice)
            {
                await store.AddSongToPlaylistAsync(choice.Id, song);
                dialog.Hide();
            }
        };

        createButton.Click += async (_, _) =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                nameError.Visibility = Visibility.Visible;
                return;
            }

            nameError.Visibility = Visibility.Collapsed;
            LibraryPlaylist created = await store.CreatePlaylistAsync(name);
            await store.AddSongToPlaylistAsync(created.Id, song);
            dialog.Hide();
        };

        await dialog.ShowAsync();
    }
}
