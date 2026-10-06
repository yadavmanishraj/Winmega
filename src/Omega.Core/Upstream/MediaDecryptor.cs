using System.Security.Cryptography;
using Omega.Core.Models;

namespace Omega.Core.Upstream;

/// <summary>
/// Replicates jiosaavn-api's link.helper.ts exactly (UPSTREAM_SPEC §3.2/§3.3):
/// the encrypted media URL is DES in ECB mode (the repo's IV is ceremony —
/// ECB ignores it), key = ASCII "38346591", standard Base64, PKCS#7 padding.
/// The decrypted URL ends in "_96.mp4"; the quality ladder is synthesised by
/// replacing the FIRST "_96" occurrence (JS String.replace semantics).
/// Images use the 50x50/150x150/500x500 token swap with https forced.
/// </summary>
public static class MediaDecryptor
{
    private static readonly byte[] DesKey = "38346591"u8.ToArray();

    private static readonly (string Token, string Label, int Kbps)[] Qualities =
    {
        ("_12", "12kbps", 12),
        ("_48", "48kbps", 48),
        ("_96", "96kbps", 96),
        ("_160", "160kbps", 160),
        ("_320", "320kbps", 320),
    };

    private static readonly string[] ImageSizes = { "50x50", "150x150", "500x500" };

    /// <summary>
    /// Decrypts an upstream <c>encrypted_media_url</c> to the plain CDN URL
    /// (the "_96" rung). Returns null for blank input or any crypto failure —
    /// a song whose URL cannot be decrypted is simply unplayable.
    /// </summary>
    public static string? DecryptMediaUrl(string? encryptedMediaUrl)
    {
        if (string.IsNullOrWhiteSpace(encryptedMediaUrl))
        {
            return null;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(encryptedMediaUrl.Trim());
            using DES des = DES.Create();
            des.Key = DesKey;
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.PKCS7;
            using ICryptoTransform decryptor = des.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            string url = System.Text.Encoding.UTF8.GetString(plainBytes);

            // Defensive: trim anything from the first NUL (padding artefacts).
            int nul = url.IndexOf('\0');
            return (nul >= 0 ? url[..nul] : url).Trim();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the full stream-quality ladder for a song. When upstream's
    /// <c>320kbps</c> flag is false the _320 rung is omitted (it 403s);
    /// callers should still fall back descending on HTTP errors.
    /// </summary>
    public static IReadOnlyList<QualityUrl> BuildQualityUrls(string? encryptedMediaUrl, bool is320Kbps)
    {
        string? plain = DecryptMediaUrl(encryptedMediaUrl);
        if (plain is null)
        {
            return Array.Empty<QualityUrl>();
        }

        if (!plain.Contains("_96", StringComparison.Ordinal))
        {
            // No ladder token — the decrypted URL is all we have.
            return new[] { new QualityUrl("96kbps", 96, plain) };
        }

        var urls = new List<QualityUrl>(Qualities.Length);
        foreach ((string token, string label, int kbps) in Qualities)
        {
            if (kbps == 320 && !is320Kbps)
            {
                continue;
            }

            urls.Add(new QualityUrl(label, kbps, ReplaceFirst(plain, "_96", token)));
        }

        return urls;
    }

    /// <summary>Builds the 50/150/500 image set from one upstream image URL.</summary>
    public static ImageSet BuildImageSet(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return ImageSet.Empty;
        }

        string https = ForceHttps(imageUrl.Trim());
        return new ImageSet(
            Small: SwapSizeToken(https, ImageSizes[0]),
            Medium: SwapSizeToken(https, ImageSizes[1]),
            Large: SwapSizeToken(https, ImageSizes[2]));
    }

    private static string SwapSizeToken(string url, string size)
    {
        // Upstream URLs contain either 150x150 or 50x50; swap whichever is present.
        int idx = url.IndexOf("150x150", StringComparison.Ordinal);
        if (idx >= 0)
        {
            return string.Concat(url.AsSpan(0, idx), size, url.AsSpan(idx + "150x150".Length));
        }

        idx = url.IndexOf("50x50", StringComparison.Ordinal);
        if (idx >= 0)
        {
            return string.Concat(url.AsSpan(0, idx), size, url.AsSpan(idx + "50x50".Length));
        }

        return url;
    }

    private static string ForceHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? string.Concat("https://", url.AsSpan("http://".Length))
            : url;

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int idx = text.IndexOf(oldValue, StringComparison.Ordinal);
        return idx < 0
            ? text
            : string.Concat(text.AsSpan(0, idx), newValue, text.AsSpan(idx + oldValue.Length));
    }
}
