using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omega.ViewModels;

namespace Omega.Views;

/// <summary>
/// Settings page (design §9.6). The ViewModel persists choices to
/// LocalSettings; this page only forwards theme changes to the shell
/// (App applies them to the root element).
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        ViewModel.ThemeModeChanged += mode => ((App)Application.Current).ApplyThemeMode(mode);
    }

    public SettingsViewModel ViewModel { get; }
}
