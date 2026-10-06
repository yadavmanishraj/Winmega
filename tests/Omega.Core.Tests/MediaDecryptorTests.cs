using System.Security.Cryptography;
using System.Text;
using Omega.Core.Models;
using Omega.Core.Upstream;
using Xunit;

namespace Omega.Core.Tests;

public class MediaDecryptorTests
{
    private const string PlainUrl = "https://aac.saavncdn.com/450/0123456789abcdef0123456789abcdef_96.mp4";

    /// <summary>
    /// Encrypts with the same algorithm/key the decryptor must invert
    /// (DES-ECB, PKCS#7, key ASCII "38346591" — UPSTREAM_SPEC §3.2), so
    /// the round-trip test needs no captured upstream ciphertext.
    /// </summary>
    private static string Encrypt(string plain)
    {
        using DES des = DES.Create();
        des.Key = "38346591"u8.ToArray();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        byte[] input = Encoding.UTF8.GetBytes(plain);
        using ICryptoTransform encryptor = des.CreateEncryptor();
        byte[] cipher = encryptor.TransformFinalBlock(input, 0, input.Length);
        return Convert.ToBase64String(cipher);
    }

    [Fact]
    public void Decrypt_RoundTrips_PlainUrl()
    {
        string? decrypted = MediaDecryptor.DecryptMediaUrl(Encrypt(PlainUrl));
        Assert.Equal(PlainUrl, decrypted);
    }

    [Fact]
    public void BuildQualityUrls_ProducesFullLadder_WithFirstOccurrenceReplacement()
    {
        IReadOnlyList<QualityUrl> urls = MediaDecryptor.BuildQualityUrls(Encrypt(PlainUrl), is320Kbps: true);

        Assert.Equal(5, urls.Count);
        Assert.Equal(
            new[] { "12kbps", "48kbps", "96kbps", "160kbps", "320kbps" },
            urls.Select(u => u.Label).ToArray());
        Assert.Equal(
            PlainUrl.Replace("_96", "_320"),
            urls.Single(u => u.Kbps == 320).Url);
        Assert.Equal(PlainUrl, urls.Single(u => u.Kbps == 96).Url);
    }

    [Fact]
    public void BuildQualityUrls_Caps320_WhenFlagFalse()
    {
        IReadOnlyList<QualityUrl> urls = MediaDecryptor.BuildQualityUrls(Encrypt(PlainUrl), is320Kbps: false);

        Assert.Equal(4, urls.Count);
        Assert.DoesNotContain(urls, u => u.Kbps == 320);
    }

    [Fact]
    public void Decrypt_BlankOrGarbage_ReturnsNull_AndEmptyLadder()
    {
        Assert.Null(MediaDecryptor.DecryptMediaUrl(null));
        Assert.Null(MediaDecryptor.DecryptMediaUrl(""));
        Assert.Null(MediaDecryptor.DecryptMediaUrl("not base64!!"));
        Assert.Empty(MediaDecryptor.BuildQualityUrls(null, is320Kbps: true));
        Assert.Empty(MediaDecryptor.BuildQualityUrls("AAAA", is320Kbps: true)); // valid base64, invalid DES block
    }

    [Fact]
    public void BuildImageSet_SwapsSizeToken_AndForcesHttps()
    {
        ImageSet set = MediaDecryptor.BuildImageSet("http://c.saavncdn.com/430/Album-150x150.jpg");

        Assert.Equal("https://c.saavncdn.com/430/Album-50x50.jpg", set.Small);
        Assert.Equal("https://c.saavncdn.com/430/Album-150x150.jpg", set.Medium);
        Assert.Equal("https://c.saavncdn.com/430/Album-500x500.jpg", set.Large);
        Assert.Equal(set.Large, set.Best);
    }

    [Fact]
    public void BuildImageSet_Blank_ReturnsEmpty()
    {
        Assert.Null(MediaDecryptor.BuildImageSet(null).Best);
        Assert.Null(MediaDecryptor.BuildImageSet("  ").Best);
    }
}
