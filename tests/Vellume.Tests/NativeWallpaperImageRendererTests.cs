using System.Drawing;
using System.Drawing.Imaging;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class NativeWallpaperImageRendererTests
{
    [Fact]
    public void Png_RendersToNativeBitmapWithVisualAdjustments()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"VellumeNativeImage-{Guid.NewGuid():N}.png");
        nint bitmapHandle = nint.Zero;
        try
        {
            using (var source = new Bitmap(8, 4))
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.CornflowerBlue);
                source.Save(path, ImageFormat.Png);
            }

            bitmapHandle = NativeWallpaperImageRenderer.Render(
                path,
                64,
                64,
                new PlaybackOptions
                {
                    Fit = WallpaperFit.Fill,
                    Brightness = 0.8,
                    Contrast = 1.1,
                    Saturation = 0.7
                });

            Assert.NotEqual(nint.Zero, bitmapHandle);
            using var rendered = Image.FromHbitmap(bitmapHandle);
            Assert.Equal(64, rendered.Width);
            Assert.Equal(64, rendered.Height);
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
