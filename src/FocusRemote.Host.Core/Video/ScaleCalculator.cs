namespace FocusRemote.Host.Core.Video;

/// <summary>Computes the encode size: fit the source inside the viewer's max size, keep aspect ratio, even dimensions.</summary>
public static class ScaleCalculator
{
    /// <summary>Smallest encoded dimension (H.264 hardware encoders reject tiny frames).</summary>
    public const int MinDimension = 16;

    /// <summary>
    /// Never upscales. <paramref name="maxWidth"/>/<paramref name="maxHeight"/> &lt;= 0 means unbounded on that axis.
    /// Result is even and at least <see cref="MinDimension"/> on each axis.
    /// </summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);

        double scale = 1.0;
        if (maxWidth > 0)
            scale = Math.Min(scale, (double)maxWidth / sourceWidth);
        if (maxHeight > 0)
            scale = Math.Min(scale, (double)maxHeight / sourceHeight);

        int width = MakeEven((int)Math.Floor(sourceWidth * scale + 1e-9));
        int height = MakeEven((int)Math.Floor(sourceHeight * scale + 1e-9));
        return (Math.Max(MinDimension, width), Math.Max(MinDimension, height));
    }

    private static int MakeEven(int value) => value & ~1;
}
