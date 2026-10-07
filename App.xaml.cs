using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Omega.Core.Upstream;
using Omega.ViewModels;

namespace Omega;

/// <summary>
/// Application entry point and composition root (design §3.3):
/// explicit service registrations only — no assembly scanning, no
/// open generics, no runtime service location scattered in pages.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // Dark-first product theme (design §10); runtime switching lands
        // with the Settings page and applies to the root element.
        RequestedTheme = ApplicationTheme.Dark;
        Services = BuildServices();
    }

    /// <summary>The single, explicitly-built service provider.</summary>
    public IServiceProvider Services { get; }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        // One singleton client (one HttpClient) for all upstream calls.
        services.AddSingleton(_ => new JioSaavnClient());
        services.AddTransient<ShellViewModel>();
        // Page ViewModels (real-data pages). Their remaining ctor
        // dependencies — IPlaybackGateway and ILibraryStore — are
        // registered by the playback/persistence workstreams.
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<DetailViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        ApplyPersistedThemeMode();
        _window.Activate();
    }

    /// <summary>
    /// Applies a theme mode ("System" / "Dark" / "Light") to the
    /// window's root element (design §9.6: runtime theme switch =
    /// RequestedTheme on the root element, cascading to every page).
    /// </summary>
    public void ApplyThemeMode(string mode)
    {
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private void ApplyPersistedThemeMode()
    {
        try
        {
            if (Windows.Storage.ApplicationData.Current.LocalSettings.Values
                    .TryGetValue(SettingsViewModel.ThemeModeKey, out object? value) &&
                value is string mode)
            {
                ApplyThemeMode(mode);
            }
        }
        catch (Exception)
        {
            // No LocalSettings (unpackaged dev run) — keep the
            // dark-first default from the constructor.
        }
    }
}
