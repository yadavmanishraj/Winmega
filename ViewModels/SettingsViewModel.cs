using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Storage;

namespace Omega.ViewModels;

/// <summary>
/// Settings (design §9.6), persisted to
/// <see cref="ApplicationData.LocalSettings"/> as primitives — no
/// serialization, nothing reflection-based. Stream quality stores
/// the Core quality-ladder label (<c>"12kbps"</c> … <c>"320kbps"</c>,
/// or <c>"Auto"</c> for the ladder-with-fallback default) so it can
/// be handed straight to <c>Song.StreamUrlFor</c>. Theme mode stores
/// <c>"System"</c> / <c>"Dark"</c> / <c>"Light"</c>; applying it to the
/// visual tree is the page's job via <c>App.ApplyThemeMode</c> (the
/// shell owns the root element, design §9.6).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    public const string StreamQualityKey = "StreamQuality";
    public const string ThemeModeKey = "ThemeMode";

    private static readonly string[] QualityValues =
        { "Auto", "12kbps", "48kbps", "96kbps", "160kbps", "320kbps" };

    /// <summary>Raised after the theme mode changes; the page applies it to the root element.</summary>
    public event Action<string>? ThemeModeChanged;

    public SettingsViewModel()
    {
        QualityOptions = new[]
        {
            Res.Get("QualityAuto"),
            Res.Format("QualityKbpsFormat", 12),
            Res.Format("QualityKbpsFormat", 48),
            Res.Format("QualityKbpsFormat", 96),
            Res.Format("QualityKbpsFormat", 160),
            Res.Format("QualityKbpsFormat", 320),
        };
        ThemeOptions = new[]
        {
            Res.Get("ThemeSystem"),
            Res.Get("ThemeDark"),
            Res.Get("ThemeLight"),
        };

        // Assigned through the generated properties (the generator owns
        // the backing fields for partial properties). The change hooks
        // re-persist the same values — harmless — and ThemeModeChanged
        // has no subscribers yet during construction.
        string quality = ReadSetting(StreamQualityKey, "Auto");
        int qualityIndex = Array.IndexOf(QualityValues, quality);
        SelectedQualityIndex = qualityIndex >= 0 ? qualityIndex : 0;

        string theme = ReadSetting(ThemeModeKey, "System");
        ThemeIndex = theme switch
        {
            "Dark" => 1,
            "Light" => 2,
            _ => 0,
        };
    }

    public string[] QualityOptions { get; }

    public string[] ThemeOptions { get; }

    /// <summary>"Omega" — the product wordmark (plain resw key, not the x:Uid entry).</summary>
    public string AppName => Res.Get("AppName");

    /// <summary>Package version for the About block (packaged app identity).</summary>
    public string VersionText
    {
        get
        {
            try
            {
                Windows.ApplicationModel.PackageVersion v =
                    Windows.ApplicationModel.Package.Current.Id.Version;
                return string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    [ObservableProperty]
    public partial int SelectedQualityIndex { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>The stored theme-mode value for the current <see cref="ThemeIndex"/>.</summary>
    public string ThemeMode => ThemeIndex switch
    {
        1 => "Dark",
        2 => "Light",
        _ => "System",
    };

    partial void OnSelectedQualityIndexChanged(int value)
    {
        if (value >= 0 && value < QualityValues.Length)
        {
            WriteSetting(StreamQualityKey, QualityValues[value]);
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        WriteSetting(ThemeModeKey, ThemeMode);
        ThemeModeChanged?.Invoke(ThemeMode);
    }

    private static string ReadSetting(string key, string fallback)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) &&
                value is string text
                ? text
                : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void WriteSetting(string key, string value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch (Exception)
        {
            // Unpackaged dev runs have no LocalSettings — settings
            // simply don't persist there.
        }
    }
}
