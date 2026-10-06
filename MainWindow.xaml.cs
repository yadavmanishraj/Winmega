using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omega.ViewModels;
using Omega.Views;
using Windows.Graphics;

namespace Omega;

/// <summary>
/// Shell window (design §9.1): command strip + SelectorBar page switch +
/// content Frame + floating Now Playing bar, over a Mica backdrop.
/// Code-behind does navigation/event wiring only — no business logic.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        // Resolved once from the composition root (design §3.3), before
        // InitializeComponent so compiled x:Bind can read it.
        ViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();

        // Sized deliberately (WinUI has no SizeToContent): 1280x800 sits
        // in the multi-pane rubric band (design §9.1). The prescribed
        // refinement — scaling by GetDpiForWindow via CsWin32 source-
        // generated P/Invoke — lands with the windowing pass.
        AppWindow.Resize(new SizeInt32(1280, 800));

        ContentFrame.Navigate(typeof(HomePage));
    }

    /// <summary>Shell view model (bound via x:Bind from the Now Playing bar).</summary>
    public ShellViewModel ViewModel { get; }

    private void DestinationBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item || item.Tag is not string tag)
        {
            return;
        }

        // Static page switch — no reflection-based navigation (AOT rule).
        Type pageType = tag switch
        {
            "search" => typeof(SearchPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder until PlayerService lands (design §7): flips the
        // shell's IsPlaying state through the generated command.
        ViewModel.TogglePlayPauseCommand.Execute(null);
    }
}
