using Omega.Core.Media;
using Xunit;

namespace Omega.Core.Tests;

public class PaletteQuantizerTests
{
    /// <summary>Builds a BGRA8 buffer filled with one colour.</summary>
    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = a;
        }

        return pixels;
    }

    private static void SetPixel(byte[] pixels, int index, byte r, byte g, byte b, byte a = 255)
    {
        pixels[index * 4] = b;
        pixels[index * 4 + 1] = g;
        pixels[index * 4 + 2] = r;
        pixels[index * 4 + 3] = a;
    }

    [Fact]
    public void SolidColour_DominantIsThatColour_AndDarkIsScaled()
    {
        var palette = PaletteQuantizer.Quantize(Solid(8, 8, r: 100, g: 150, b: 200), 8, 8);

        Assert.Equal(new RgbColor(100, 150, 200), palette.Dominant);
        // A single colour is also the most vibrant thing present.
        Assert.Equal(new RgbColor(100, 150, 200), palette.Vibrant);
        // Dark = Dominant x 0.32 per channel.
        Assert.Equal(new RgbColor(32, 48, 64), palette.Dark);
    }

    [Fact]
    public void TwoColours_DominantIsOne_VibrantIsTheSaturatedOne()
    {
        // Left half neutral grey, right half saturated red, equal areas.
        var pixels = Solid(8, 4, r: 128, g: 128, b: 128);
        for (int i = 16; i < 32; i++)
        {
            SetPixel(pixels, i, r: 255, g: 0, b: 0);
        }

        var palette = PaletteQuantizer.Quantize(pixels, 8, 4);

        Assert.Contains(palette.Dominant, new[] { new RgbColor(128, 128, 128), new RgbColor(255, 0, 0) });
        Assert.Equal(new RgbColor(255, 0, 0), palette.Vibrant);
    }

    [Fact]
    public void TransparentPixels_AreIgnored()
    {
        // Half the buffer is fully transparent red; only opaque blue counts.
        var pixels = Solid(4, 4, r: 255, g: 0, b: 0, a: 0);
        for (int i = 8; i < 16; i++)
        {
            SetPixel(pixels, i, r: 20, g: 40, b: 220);
        }

        var palette = PaletteQuantizer.Quantize(pixels, 4, 4);

        Assert.Equal(new RgbColor(20, 40, 220), palette.Dominant);
        Assert.Equal(new RgbColor(20, 40, 220), palette.Vibrant);
    }

    [Fact]
    public void EmptySpan_ReturnsFallback_WithoutThrowing()
    {
        var palette = PaletteQuantizer.Quantize(ReadOnlySpan<byte>.Empty, 0, 0);

        // The neutral fallback family: a mid grey whose Dark anchor is darker.
        Assert.Equal(new RgbColor(128, 128, 132), palette.Dominant);
        Assert.True(palette.Dark.R < palette.Dominant.R);
    }

    [Fact]
    public void AllTransparent_ReturnsFallback_WithoutThrowing()
    {
        var palette = PaletteQuantizer.Quantize(Solid(4, 4, r: 250, g: 10, b: 10, a: 0), 4, 4);

        Assert.Equal(new RgbColor(128, 128, 132), palette.Dominant);
    }

    [Fact]
    public void Mid_BlendsMeanTowardDominant()
    {
        // Three quarters white, one quarter black: dominant is white,
        // the mean is light grey, and Mid sits halfway between them.
        var pixels = Solid(4, 4, r: 255, g: 255, b: 255);
        for (int i = 12; i < 16; i++)
        {
            SetPixel(pixels, i, r: 0, g: 0, b: 0);
        }

        var palette = PaletteQuantizer.Quantize(pixels, 4, 4);

        Assert.Equal(new RgbColor(255, 255, 255), palette.Dominant);
        // mean = 191 per channel; Mid = round((191 + 255) / 2) = 223.
        Assert.Equal(new RgbColor(223, 223, 223), palette.Mid);
    }
}
