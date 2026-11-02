using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using AnotherSurface.Core;

namespace AnotherSurface.Windows;

internal static class NativeWallpaperImageRenderer
{
    internal static nint Render(
        string sourcePath,
        int targetWidth,
        int targetHeight,
        PlaybackOptions options)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWidth),
                "네이티브 이미지 출력 크기는 0보다 커야 합니다.");
        }

        using var fileImage = Image.FromFile(sourcePath);
        using var source = new Bitmap(fileImage);
        return Render(
            source,
            targetWidth,
            targetHeight,
            options);
    }

    internal static nint Render(
        Image source,
        int targetWidth,
        int targetHeight,
        PlaybackOptions options)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWidth),
                "The wallpaper output size must be greater than zero.");
        }

        using var output = new Bitmap(
            targetWidth,
            targetHeight,
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Black);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        var destination = WallpaperLayout.Calculate(
            source.Width,
            source.Height,
            targetWidth,
            targetHeight,
            options.Fit);
        using var imageAttributes = new ImageAttributes();
        imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
        imageAttributes.SetColorMatrix(
            BuildColorMatrix(options),
            ColorMatrixFlag.Default,
            ColorAdjustType.Bitmap);
        graphics.DrawImage(
            source,
            destination,
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            imageAttributes);
        graphics.Flush();
        return output.GetHbitmap(Color.Black);
    }

    private static ColorMatrix BuildColorMatrix(
        PlaybackOptions options)
    {
        var brightness = (float)Math.Clamp(
            options.Brightness,
            0.25,
            1.25);
        var contrast = (float)Math.Clamp(
            options.Contrast,
            0.5,
            1.5);
        var saturation = (float)Math.Clamp(
            options.Saturation,
            0,
            1.5);
        var inverseSaturation = 1 - saturation;
        var red = 0.3086f * inverseSaturation;
        var green = 0.6094f * inverseSaturation;
        var blue = 0.0820f * inverseSaturation;
        var scale = brightness * contrast;
        var offset = 0.5f * (1 - contrast);

        return new ColorMatrix(
        [
            [(red + saturation) * scale, red * scale,
                red * scale, 0, 0],
            [green * scale, (green + saturation) * scale,
                green * scale, 0, 0],
            [blue * scale, blue * scale,
                (blue + saturation) * scale, 0, 0],
            [0, 0, 0, 1, 0],
            [offset, offset, offset, 0, 1]
        ]);
    }
}

internal static class WallpaperLayout
{
    internal static Rectangle Calculate(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        WallpaperFit fit)
    {
        if (sourceWidth <= 0 ||
            sourceHeight <= 0 ||
            targetWidth <= 0 ||
            targetHeight <= 0)
        {
            return Rectangle.Empty;
        }

        if (fit == WallpaperFit.Stretch)
        {
            return new Rectangle(0, 0, targetWidth, targetHeight);
        }

        var horizontalScale = targetWidth / (double)sourceWidth;
        var verticalScale = targetHeight / (double)sourceHeight;
        var scale = fit == WallpaperFit.Fit
            ? Math.Min(horizontalScale, verticalScale)
            : Math.Max(horizontalScale, verticalScale);
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return new Rectangle(
            (targetWidth - width) / 2,
            (targetHeight - height) / 2,
            width,
            height);
    }
}
