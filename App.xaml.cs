using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Omega.Core.Playback;
using Omega.Core.Upstream;
using Omega.Services;
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
        // Playback (design §7): the engine is a singleton; pages and
        // view models depend on the IPlaybackGateway seam, and the
        // concrete registration lets the Now Playing page reach the
        // queue/player surface beyond the seam.
        services.AddSingleton<PlayerService>();
        services.AddSingleton<IPlaybackGateway>(provider => provider.GetRequiredService<PlayerService>());
        services.AddTransient<ShellViewModel>();
        services.AddTransient<PlayerViewModel>();
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
