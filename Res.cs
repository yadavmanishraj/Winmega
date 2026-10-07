using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace Omega;

/// <summary>
/// Access to <c>Strings/en-us/Resources.resw</c> for strings consumed
/// from code (ViewModels, dialogs) rather than XAML. XAML-side strings
/// keep using <c>x:Uid</c> (design §10); this helper covers the rest —
/// greetings, format strings with placeholders, dialog copy. Keys here
/// are plain resw entry names (no property suffix).
/// </summary>
public static class Res
{
    private static readonly ResourceLoader Loader = ResourceLoader.GetForViewIndependentUse();

    /// <summary>Returns the resw string for <paramref name="key"/> (empty when missing).</summary>
    public static string Get(string key) => Loader.GetString(key);

    /// <summary>Returns the resw format string for <paramref name="key"/> formatted with the current culture.</summary>
    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Loader.GetString(key), args);
}
