namespace Vellume.Core;

public static class WallpaperValidator
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm" };

    public static WallpaperKind DetectKind(string path)
    {
        if (Directory.Exists(path))
        {
            throw new NotSupportedException("폴더 및 HTML 월페이퍼는 지원하지 않습니다.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("월페이퍼 소스를 찾지 못했습니다.", path);
        }

        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension))
        {
            return WallpaperKind.Image;
        }

        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperKind.Gif;
        }

        if (VideoExtensions.Contains(extension))
        {
            return WallpaperKind.Video;
        }

        throw new NotSupportedException(
            $"The '{extension}' file type is not supported by Vellume.");
    }

    public static WallpaperItem CreateItem(string path, bool isBuiltIn = false)
    {
        var kind = DetectKind(path);
        var normalizedPath = Path.GetFullPath(path);
        var name = Directory.Exists(normalizedPath)
            ? new DirectoryInfo(normalizedPath).Name
            : Path.GetFileNameWithoutExtension(normalizedPath);

        return new WallpaperItem
        {
            Name = name,
            SourcePath = normalizedPath,
            Kind = kind,
            IsBuiltIn = isBuiltIn
        };
    }
}
