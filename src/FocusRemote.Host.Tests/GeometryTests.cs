using FocusRemote.Host.Core.Input;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Video;

namespace FocusRemote.Host.Tests;

public sealed class GeometryTests
{
    [Theory]
    [InlineData(0, 0, 100, 50)]
    [InlineData(65535, 65535, 1379, 1129)]
    [InlineData(32768, 0, 740, 50)]
    public void ToScreen_MapsNormalizedEdgesToFirstAndLastPixel(int x, int y, int expectedX, int expectedY)
    {
        var target = new PixelRect(100, 50, 1280, 1080);
        Assert.Equal((expectedX, expectedY), CoordinateMapper.ToScreen((ushort)x, (ushort)y, target));
    }

    [Fact]
    public void VirtualDesktopAbsolute_HandlesNegativeOrigin()
    {
        // Two monitors: secondary 1920x1080 on the left (x = -1920), primary 2560x1440.
        var virtualScreen = new PixelRect(-1920, 0, 4480, 1440);
        Assert.Equal((0, 0), CoordinateMapper.ToVirtualDesktopAbsolute(-1920, 0, virtualScreen));
        Assert.Equal((65535, 65535), CoordinateMapper.ToVirtualDesktopAbsolute(2559, 1439, virtualScreen));
        (int ax, _) = CoordinateMapper.ToVirtualDesktopAbsolute(0, 0, virtualScreen);
        Assert.Equal((int)Math.Round(1920 * 65535.0 / 4479), ax);
    }

    [Fact]
    public void VirtualDesktopAbsolute_ClampsOutside()
    {
        var virtualScreen = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal((0, 65535), CoordinateMapper.ToVirtualDesktopAbsolute(-50, 5000, virtualScreen));
    }

    [Fact]
    public void NormalizedToAbsolute_OnSecondaryMonitor_RoundTripsToPixel()
    {
        var virtualScreen = new PixelRect(-1920, 0, 4480, 1440);
        var monitor = new PixelRect(-1920, 0, 1920, 1080);
        (int ax, int ay) = CoordinateMapper.ToVirtualDesktopAbsolute(65535, 65535, monitor, virtualScreen);
        // Back to pixels the way Windows does: pixel = abs * (extent - 1) / 65535.
        int px = (int)Math.Round(ax * 4479 / 65535.0) - 1920;
        int py = (int)Math.Round(ay * 1439 / 65535.0);
        Assert.Equal((-1, 1079), (px, py));
    }

    [Theory]
    [InlineData(2560, 1440, 1920, 1080, 1920, 1080)]
    [InlineData(2560, 1600, 1920, 1080, 1728, 1080)]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(801, 601, 0, 0, 800, 600)]
    [InlineData(3000, 1000, 1000, 0, 1000, 332)]
    [InlineData(1000, 3000, 0, 1000, 332, 1000)]
    [InlineData(10, 4, 1920, 1080, 16, 16)]
    public void Fit_KeepsAspectEvenAndNeverUpscales(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), ScaleCalculator.Fit(sw, sh, mw, mh));
    }

    [Fact]
    public void Fit_ResultIsAlwaysEvenAndWithinBounds()
    {
        var random = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            int sw = random.Next(16, 8000), sh = random.Next(16, 8000);
            int mw = random.Next(16, 4000), mh = random.Next(16, 4000);
            (int w, int h) = ScaleCalculator.Fit(sw, sh, mw, mh);
            Assert.Equal(0, w % 2);
            Assert.Equal(0, h % 2);
            Assert.True(w <= Math.Max(ScaleCalculator.MinDimension, Math.Min(sw, mw)));
            Assert.True(h <= Math.Max(ScaleCalculator.MinDimension, Math.Min(sh, mh)));
        }
    }
}
