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
            var indexPath = Path.Combine(path, "index.html");
            if (!File.Exists(indexPath))
            {
                throw new NotSupportedException(
                    "A web wallpaper folder must contain index.html.");
            }

            return WallpaperKind.Web;
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

        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperKind.Web;
        }

        throw new NotSupportedException(
            $"The '{extension}' file type is not supported by Vellume.");
    }

    public static string ResolveWebEntryPoint(string sourcePath)
        => Directory.Exists(sourcePath)
            ? Path.Combine(sourcePath, "index.html")
            : sourcePath;

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
