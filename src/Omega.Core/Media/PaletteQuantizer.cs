namespace Omega.Core.Media;

/// <summary>One 8-bit-per-channel RGB colour (the palette's currency type).</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);

/// <summary>
/// The four colour roles the Now Playing FX layer consumes: the
/// artwork's main colour, its most vivid colour, a mid-tone bridge, and
/// a dark background anchor derived from the dominant colour.
/// </summary>
public sealed record PaletteColors(RgbColor Dominant, RgbColor Vibrant, RgbColor Mid, RgbColor Dark);

/// <summary>
/// Deterministic, allocation-light palette extraction over raw BGRA8
/// pixels (design: NOW_PLAYING_FX §1). A 4-bits-per-channel histogram
/// (4096 bins, each accumulating count + per-channel sums so a bin's
/// colour is its true average, not its cube centre) drives the roles:
///
///   Dominant — the fullest bin.
///   Vibrant  — the bin maximising HSV saturation × pixel count, so a
///              tiny vivid speck cannot outrank a substantial vivid area.
///   Mid      — the mean colour of all counted pixels pulled 50% toward
///              Dominant: a mid-tone that always belongs to the
///              dominant family instead of drifting to an unrelated hue.
///   Dark     — Dominant darkened toward black (×0.32 per channel);
///              the background anchor the FX gradient sits on.
///
/// Pixels with alpha &lt; 32 count as absent. Empty or degenerate input
/// (no countable pixels, non-positive dimensions) returns a neutral
/// grey family rather than throwing. No reflection, no floating-point
/// nondeterminism beyond fixed iteration order — identical input always
/// yields an identical palette.
/// </summary>
public static class PaletteQuantizer
{
    private const int BitsPerChannel = 4;
    private const int BinCount = 1 << (BitsPerChannel * 3); // 4096
    private const byte MinAlpha = 32;
    private const double DarkScale = 0.32;

    // Neutral grey family for absent/degenerate artwork: quiet,
    // brand-neutral, and dark enough at the anchor for white text.
    private static readonly PaletteColors Fallback = new(
        Dominant: new RgbColor(128, 128, 132),
        Vibrant: new RgbColor(128, 128, 132),
        Mid: new RgbColor(96, 96, 100),
        Dark: new RgbColor(41, 41, 42));

    public static PaletteColors Quantize(ReadOnlySpan<byte> bgraPixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgraPixels.Length < 4)
        {
            return Fallback;
        }

        // Never read past the buffer if the caller's dimensions overstate it.
        long pixelBudget = Math.Min((long)width * height, bgraPixels.Length / 4);

        var counts = new int[BinCount];
        var sumR = new long[BinCount];
        var sumG = new long[BinCount];
        var sumB = new long[BinCount];
        long totalR = 0, totalG = 0, totalB = 0;
        long valid = 0;

        for (long i = 0; i < pixelBudget; i++)
        {
            int offset = (int)(i * 4);
            byte b = bgraPixels[offset];
            byte g = bgraPixels[offset + 1];
            byte r = bgraPixels[offset + 2];
            byte a = bgraPixels[offset + 3];
            if (a < MinAlpha)
            {
                continue;
            }

            int bin = ((r >> (8 - BitsPerChannel)) << (BitsPerChannel * 2))
                    | ((g >> (8 - BitsPerChannel)) << BitsPerChannel)
                    | (b >> (8 - BitsPerChannel));
            counts[bin]++;
            sumR[bin] += r;
            sumG[bin] += g;
            sumB[bin] += b;
            totalR += r;
            totalG += g;
            totalB += b;
            valid++;
        }

        if (valid == 0)
        {
            return Fallback;
        }

        // Single pass for Dominant (fullest bin) and Vibrant (saturation
        // × count); strict > keeps the lowest bin index on ties, so the
        // result is independent of anything but the input bytes.
        int dominantBin = -1;
        int vibrantBin = -1;
        double bestVibrantScore = -1;
        for (int bin = 0; bin < BinCount; bin++)
        {
            int count = counts[bin];
            if (count == 0)
            {
                continue;
            }

            if (dominantBin < 0 || count > counts[dominantBin])
            {
                dominantBin = bin;
            }

            double saturation = Saturation(
                (double)sumR[bin] / count, (double)sumG[bin] / count, (double)sumB[bin] / count);
            double score = saturation * count;
            if (score > bestVibrantScore)
            {
                bestVibrantScore = score;
                vibrantBin = bin;
            }
        }

        RgbColor dominant = BinAverage(dominantBin, counts, sumR, sumG, sumB);
        RgbColor vibrant = BinAverage(vibrantBin, counts, sumR, sumG, sumB);

        var mean = new RgbColor(
            (byte)Math.Round((double)totalR / valid),
            (byte)Math.Round((double)totalG / valid),
            (byte)Math.Round((double)totalB / valid));
        var mid = new RgbColor(
            (byte)Math.Round((mean.R + dominant.R) / 2.0),
            (byte)Math.Round((mean.G + dominant.G) / 2.0),
            (byte)Math.Round((mean.B + dominant.B) / 2.0));

        var dark = new RgbColor(
            (byte)Math.Round(dominant.R * DarkScale),
            (byte)Math.Round(dominant.G * DarkScale),
            (byte)Math.Round(dominant.B * DarkScale));

        return new PaletteColors(dominant, vibrant, mid, dark);
    }

    private static RgbColor BinAverage(int bin, int[] counts, long[] sumR, long[] sumG, long[] sumB)
    {
        int count = counts[bin];
        return new RgbColor(
            (byte)Math.Round((double)sumR[bin] / count),
            (byte)Math.Round((double)sumG[bin] / count),
            (byte)Math.Round((double)sumB[bin] / count));
    }

    /// <summary>HSV saturation of an RGB triple in 0..255 space (0 when black).</summary>
    private static double Saturation(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        if (max <= 0)
        {
            return 0;
        }

        double min = Math.Min(r, Math.Min(g, b));
        return (max - min) / max;
    }
}
