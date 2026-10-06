namespace Omega.Core.Models;

/// <summary>
/// The three artwork sizes JioSaavn serves for every entity
/// (50x50, 150x150, 500x500), derived from one upstream image URL
/// by swapping the size token (see Upstream.MediaDecryptor.BuildImageSet).
/// Any size may be null when upstream sent no image.
/// </summary>
public sealed record ImageSet(string? Small, string? Medium, string? Large)
{
    public static readonly ImageSet Empty = new(null, null, null);

    /// <summary>Largest available image, falling back to smaller sizes.</summary>
    public string? Best => Large ?? Medium ?? Small;
}

/// <summary>One rung of the synthesised stream-quality ladder.</summary>
public sealed record QualityUrl(string Label, int Kbps, string Url);
