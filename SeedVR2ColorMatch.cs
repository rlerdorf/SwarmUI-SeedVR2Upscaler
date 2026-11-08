using ImageMagick;

namespace SeedVR2Upscaler;

/// <summary>Global CIELAB color-statistics matching (Reinhard et al. 2001), used to harmonize a tiled
/// SeedVR2 upscale against its source image in a single pass instead of grading each tile against
/// only its own local crop (see <see cref="SeedVR2ImageUpscaler.CreateNode"/> for why per-tile
/// grading is disabled).</summary>
public static class SeedVR2ColorMatch
{
    /// <summary>Mean and standard deviation of each CIELAB channel across a whole image.</summary>
    /// <param name="LMean">Mean lightness.</param>
    /// <param name="LStd">Lightness standard deviation.</param>
    /// <param name="AMean">Mean green-red chroma.</param>
    /// <param name="AStd">Green-red chroma standard deviation.</param>
    /// <param name="BMean">Mean blue-yellow chroma.</param>
    /// <param name="BStd">Blue-yellow chroma standard deviation.</param>
    public readonly record struct LabStats(double LMean, double LStd, double AMean, double AStd, double BMean, double BStd);

    /// <summary>D65 white point X component.</summary>
    private const double WhiteX = 0.95047;

    /// <summary>D65 white point Z component.</summary>
    private const double WhiteZ = 1.08883;

    /// <summary>CIELAB linear-segment threshold, (6/29)^3.</summary>
    private const double LabEpsilon = 216.0 / 24389.0;

    /// <summary>CIELAB linear-segment slope, (29/3)^3.</summary>
    private const double LabKappa = 24389.0 / 27.0;

    /// <summary>Number of entries in the linear-light to 8-bit sRGB lookup table.</summary>
    private const int LinearSteps = 16384;

    /// <summary>Lookup table converting an 8-bit sRGB channel value to linear light in [0, 1].</summary>
    private static readonly double[] SrgbToLinearTable = BuildSrgbToLinear();

    /// <summary>Lookup table converting quantized linear light back to an 8-bit sRGB channel value.</summary>
    private static readonly byte[] LinearToSrgbTable = BuildLinearToSrgb();

    /// <summary>Builds <see cref="SrgbToLinearTable"/>.</summary>
    private static double[] BuildSrgbToLinear()
    {
        double[] table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return table;
    }

    /// <summary>Builds <see cref="LinearToSrgbTable"/>.</summary>
    private static byte[] BuildLinearToSrgb()
    {
        byte[] table = new byte[LinearSteps];
        for (int i = 0; i < LinearSteps; i++)
        {
            double c = i / (double)(LinearSteps - 1);
            double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            table[i] = (byte)Math.Clamp(Math.Round(s * 255.0), 0, 255);
        }
        return table;
    }

    /// <summary>CIELAB forward nonlinearity.</summary>
    /// <param name="t">Normalized XYZ component.</param>
    private static double LabF(double t)
    {
        return t > LabEpsilon ? Math.Cbrt(t) : (LabKappa * t + 16.0) / 116.0;
    }

    /// <summary>CIELAB inverse nonlinearity.</summary>
    /// <param name="t">Nonlinear CIELAB f() value.</param>
    private static double LabFInv(double t)
    {
        double cubed = t * t * t;
        return cubed > LabEpsilon ? cubed : (116.0 * t - 16.0) / LabKappa;
    }

    /// <summary>Converts an 8-bit sRGB triple to CIELAB (D65).</summary>
    /// <param name="r">Red channel.</param>
    /// <param name="g">Green channel.</param>
    /// <param name="b">Blue channel.</param>
    private static (double L, double A, double B) RgbToLab(byte r, byte g, byte b)
    {
        double lr = SrgbToLinearTable[r], lg = SrgbToLinearTable[g], lb = SrgbToLinearTable[b];
        double x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / WhiteX;
        double y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb;
        double z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / WhiteZ;
        double fx = LabF(x), fy = LabF(y), fz = LabF(z);
        return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    /// <summary>Converts a linear-light channel value to an 8-bit sRGB value, with clamping.</summary>
    /// <param name="value">Linear-light value, may be out of gamut.</param>
    private static byte LinearToByte(double value)
    {
        int index = (int)(Math.Clamp(value, 0.0, 1.0) * (LinearSteps - 1) + 0.5);
        return LinearToSrgbTable[index];
    }

    /// <summary>Converts a CIELAB triple (D65) back to an 8-bit sRGB triple, clamping out-of-gamut results.</summary>
    /// <param name="l">Lightness.</param>
    /// <param name="a">Green-red chroma.</param>
    /// <param name="bb">Blue-yellow chroma.</param>
    /// <param name="r">Output red channel.</param>
    /// <param name="g">Output green channel.</param>
    /// <param name="b">Output blue channel.</param>
    private static void LabToRgb(double l, double a, double bb, out byte r, out byte g, out byte b)
    {
        double fy = (l + 16.0) / 116.0;
        double fx = fy + a / 500.0;
        double fz = fy - bb / 200.0;
        double x = LabFInv(fx) * WhiteX;
        double y = LabFInv(fy);
        double z = LabFInv(fz) * WhiteZ;
        r = LinearToByte(3.2404542 * x - 1.5371385 * y - 0.4985314 * z);
        g = LinearToByte(-0.9692660 * x + 1.8760108 * y + 0.0415560 * z);
        b = LinearToByte(0.0556434 * x - 0.2040259 * y + 1.0572252 * z);
    }

    /// <summary>Converts a Q16 quantum to the nearest 8-bit value.</summary>
    /// <param name="value">Channel value in [0, 65535].</param>
    private static byte QuantumToByte(ushort value)
    {
        return (byte)((value + 128) / 257);
    }

    /// <summary>Converts an 8-bit value to a Q16 quantum.</summary>
    /// <param name="value">Channel value in [0, 255].</param>
    private static ushort ByteToQuantum(byte value)
    {
        return (ushort)(value * 257);
    }

    /// <summary>Returns true if the image's pixels can be read directly as sRGB red/green/blue channels.</summary>
    /// <param name="image">The image to check.</param>
    private static bool IsSrgb(IMagickImage<ushort> image)
    {
        if (image.ColorSpace != ColorSpace.sRGB)
        {
            return false;
        }
        bool red = false, green = false, blue = false;
        foreach (PixelChannel channel in image.Channels)
        {
            red |= channel == PixelChannel.Red;
            green |= channel == PixelChannel.Green;
            blue |= channel == PixelChannel.Blue;
        }
        return red && green && blue;
    }

    /// <summary>Converts the image in place to direct-class sRGB with red/green/blue (and alpha, if present)
    /// channels, so its pixels can be read and written by <see cref="ComputeLabStats"/> and
    /// <see cref="ApplyChromaTransfer"/>. Grayscale, CMYK, linear-RGB and palette images are all normalized.</summary>
    /// <param name="image">The image to convert.</param>
    public static void ConvertToSrgb(IMagickImage<ushort> image)
    {
        if (image.ColorSpace != ColorSpace.sRGB)
        {
            image.ColorSpace = ColorSpace.sRGB;
        }
        if (!IsSrgb(image))
        {
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
        }
        if (image.ClassType != ClassType.Direct)
        {
            image.ClassType = ClassType.Direct;
        }
    }

    /// <summary>Gets the offset of a channel within each pixel, or -1 if the image has no such channel.</summary>
    /// <param name="pixels">The image's pixel collection.</param>
    /// <param name="channel">The channel to find.</param>
    private static int ChannelOffset(IPixelCollection<ushort> pixels, PixelChannel channel)
    {
        return pixels.GetChannelIndex(channel) is { } index ? (int)index : -1;
    }

    /// <summary>Computes whole-image CIELAB mean and standard deviation. Fully transparent pixels are excluded.
    /// Math is done at 8-bit precision, matching the 8-bit images SeedVR2 produces.</summary>
    /// <param name="image">The image to measure. Not modified; cloned and converted only if it is not already sRGB.</param>
    public static LabStats ComputeLabStats(IMagickImage<ushort> image)
    {
        IMagickImage<ushort> srgb = image;
        if (!IsSrgb(image))
        {
            srgb = image.Clone();
            ConvertToSrgb(srgb);
        }
        try
        {
            double sumL = 0, sumA = 0, sumB = 0, sumL2 = 0, sumA2 = 0, sumB2 = 0;
            long count = 0;
            using IUnsafePixelCollection<ushort> pixels = srgb.GetPixelsUnsafe();
            int stride = (int)pixels.Channels;
            int ri = ChannelOffset(pixels, PixelChannel.Red), gi = ChannelOffset(pixels, PixelChannel.Green), bi = ChannelOffset(pixels, PixelChannel.Blue);
            int ai = ChannelOffset(pixels, PixelChannel.Alpha);
            uint width = srgb.Width;
            int height = (int)srgb.Height;
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<ushort> row = pixels.GetReadOnlyArea(0, y, width, 1);
                for (int o = 0; o < row.Length; o += stride)
                {
                    if (ai >= 0 && row[o + ai] == 0)
                    {
                        continue;
                    }
                    (double l, double a, double b) = RgbToLab(QuantumToByte(row[o + ri]), QuantumToByte(row[o + gi]), QuantumToByte(row[o + bi]));
                    sumL += l; sumA += a; sumB += b;
                    sumL2 += l * l; sumA2 += a * a; sumB2 += b * b;
                    count++;
                }
            }
            if (count == 0)
            {
                return new(0, 0, 0, 0, 0, 0);
            }
            double n = count;
            double meanL = sumL / n, meanA = sumA / n, meanB = sumB / n;
            static double std(double sumSq, double n, double mean) => Math.Sqrt(Math.Max(0.0, sumSq / n - mean * mean));
            return new(meanL, std(sumL2, n, meanL), meanA, std(sumA2, n, meanA), meanB, std(sumB2, n, meanB));
        }
        finally
        {
            if (!ReferenceEquals(srgb, image))
            {
                srgb.Dispose();
            }
        }
    }

    /// <summary>Applies a global CIELAB chroma mean/std transfer in place, moving <paramref name="image"/>'s
    /// a*/b* statistics toward <paramref name="reference"/>. Lightness is deliberately left untouched, since
    /// L* variance is resolution- and sharpness-dependent and the upscaler's added high-frequency detail
    /// should not be flattened back toward a lower-resolution source's contrast.</summary>
    /// <param name="image">The image to adjust in place. Converted to sRGB first (see <see cref="ConvertToSrgb"/>)
    /// if needed, so callers must pass an image they own, never a shared cached frame.</param>
    /// <param name="source">Statistics measured from <paramref name="image"/> itself.</param>
    /// <param name="reference">Target statistics, measured from the pre-upscale source image.</param>
    /// <param name="strength">Blend toward the fully-matched result, 0 = no change, 1 = full transfer.</param>
    /// <param name="maxScale">Cap on the per-channel standard-deviation scale factor, guarding against
    /// over-correction on near-monochrome images.</param>
    /// <returns>True if any pixel was modified.</returns>
    public static bool ApplyChromaTransfer(IMagickImage<ushort> image, LabStats source, LabStats reference, double strength = 1.0, double maxScale = 2.0)
    {
        double scaleA = Math.Clamp(reference.AStd / Math.Max(source.AStd, 1e-6), 1.0 / maxScale, maxScale);
        double scaleB = Math.Clamp(reference.BStd / Math.Max(source.BStd, 1e-6), 1.0 / maxScale, maxScale);
        // Affine form of a' = (a - srcMean) * scale + refMean, blended toward identity by strength.
        double kA = 1.0 + strength * (scaleA - 1.0);
        double cA = strength * (reference.AMean - scaleA * source.AMean);
        double kB = 1.0 + strength * (scaleB - 1.0);
        double cB = strength * (reference.BMean - scaleB * source.BMean);
        if (Math.Abs(kA - 1.0) < 1e-4 && Math.Abs(kB - 1.0) < 1e-4 && Math.Abs(cA) < 0.1 && Math.Abs(cB) < 0.1)
        {
            return false;
        }
        ConvertToSrgb(image);
        using IUnsafePixelCollection<ushort> pixels = image.GetPixelsUnsafe();
        int stride = (int)pixels.Channels;
        int ri = ChannelOffset(pixels, PixelChannel.Red), gi = ChannelOffset(pixels, PixelChannel.Green), bi = ChannelOffset(pixels, PixelChannel.Blue);
        int ai = ChannelOffset(pixels, PixelChannel.Alpha);
        // Read everything up front and write it back in one SetPixels call. Writing row by row is NOT safe
        // here: a Clone() shares its pixel cache with the original, and ImageMagick's first queued write
        // detaches the clone onto a fresh cache WITHOUT copying the existing pixels, so every row read after
        // that returns uninitialized memory. A single full-image write supplies every pixel, so it is correct
        // whether or not the cache was shared.
        ushort[] data = pixels.ToArray();
        for (int o = 0; o < data.Length; o += stride)
        {
            if (ai >= 0 && data[o + ai] == 0)
            {
                continue;
            }
            (double l, double a, double b) = RgbToLab(QuantumToByte(data[o + ri]), QuantumToByte(data[o + gi]), QuantumToByte(data[o + bi]));
            a = Math.Clamp(kA * a + cA, -128.0, 127.0);
            b = Math.Clamp(kB * b + cB, -128.0, 127.0);
            LabToRgb(l, a, b, out byte r, out byte g, out byte bl);
            data[o + ri] = ByteToQuantum(r);
            data[o + gi] = ByteToQuantum(g);
            data[o + bi] = ByteToQuantum(bl);
        }
        pixels.SetPixels(data);
        return true;
    }
}
