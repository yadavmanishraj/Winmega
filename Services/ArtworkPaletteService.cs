using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Omega.Core.Media;
using Omega.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Omega.Services;

/// <summary>
/// An artwork palette in WinRT colours, ready to feed Composition
/// brushes (the Core quantizer's <see cref="PaletteColors"/> mapped 1:1).
/// </summary>
public sealed record ArtworkPalette(Color Dominant, Color Vibrant, Color Mid, Color Dark);

/// <summary>
/// Artwork palette extraction for the Now Playing FX layer
/// (NOW_PLAYING_FX §1). The app has no other palette facility: the
/// panel's tint is a static brush, and <c>ArtworkHelper</c> only ever
/// yields a <c>BitmapImage</c> over the artwork URL — so this service
/// decodes from that same source: the bytes behind
/// <see cref="ImageSet.Best"/>, fetched through the platform URI loader
/// (<see cref="RandomAccessStreamReference"/>), downscaled to 32×32
/// BGRA8 by <see cref="BitmapDecoder"/> and reduced to colour roles by
/// <see cref="PaletteQuantizer"/>.
///
/// Contract: never throws, never blocks the caller. Extraction runs on
/// a background thread, once per song id; the result — including
/// failure, cached as null so a bad image is not retried on every panel
/// open — is memoised for the process lifetime in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of the extraction
/// tasks themselves (so concurrent callers share one decode). A
/// caller's cancellation token only abandons that caller's wait, never
/// the shared extraction.
/// </summary>
public sealed class ArtworkPaletteService
{
    private const uint PaletteSize = 32;

    private readonly ConcurrentDictionary<string, Task<ArtworkPalette?>> _cache = new();

    public async Task<ArtworkPalette?> GetPaletteAsync(Song song, CancellationToken ct = default)
    {
        if (song is null || song.Image.Best is not { } url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        Task<ArtworkPalette?> extraction = _cache.GetOrAdd(song.Id, _ => ExtractAsync(url));
        if (!ct.CanBeCanceled)
        {
            return await extraction;
        }

        try
        {
            return await extraction.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<ArtworkPalette?> ExtractAsync(string url)
    {
        try
        {
            // Decode + quantize are CPU work on a downloaded image:
            // keep them off the UI thread entirely.
            return await Task.Run(() => ExtractCoreAsync(url));
        }
        catch
        {
            // A palette is decoration; any failure (network, codec,
            // malformed image) degrades to the static tint, never to
            // an error surface.
            return null;
        }
    }

    private static async Task<ArtworkPalette?> ExtractCoreAsync(string url)
    {
        var reference = RandomAccessStreamReference.CreateFromUri(new Uri(url));
        using IRandomAccessStream stream = await reference.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = PaletteSize,
            ScaledHeight = PaletteSize,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();

        PaletteColors palette = PaletteQuantizer.Quantize(pixels, (int)PaletteSize, (int)PaletteSize);
        return new ArtworkPalette(
            ToColor(palette.Dominant),
            ToColor(palette.Vibrant),
            ToColor(palette.Mid),
            ToColor(palette.Dark));
    }

    private static Color ToColor(RgbColor color) => Color.FromArgb(255, color.R, color.G, color.B);
}
