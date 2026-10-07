using System;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Omega.ViewModels;

/// <summary>Culture-aware display formatting shared by the row/tile ViewModels.</summary>
public static class DisplayFormatting
{
    /// <summary>Track duration as m:ss (empty when unknown).</summary>
    public static string Duration(int? seconds) =>
        seconds is > 0
            ? TimeSpan.FromSeconds(seconds.Value).ToString(@"m\:ss", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Grouped count (e.g. play/follower counts); empty when unknown.</summary>
    public static string Count(long? count) =>
        count is > 0 ? count.Value.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
}

/// <summary>
/// Builds <see cref="ImageSource"/> values for x:Bind. Item ViewModels
/// expose artwork as ImageSource (not raw URL strings) so compiled
/// bindings never depend on binding-engine string→image conversion
/// under AOT/trimming.
/// </summary>
public static class ArtworkHelper
{
    public static ImageSource? From(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : new BitmapImage(new Uri(url));
}
