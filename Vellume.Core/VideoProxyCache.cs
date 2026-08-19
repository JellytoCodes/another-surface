using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Vellume.Core;

public sealed record VideoPlaybackPreparation(
    WallpaperItem Wallpaper,
    bool UsedProxy,
    bool GeneratedProxy,
    string Detail);

public sealed record VideoStreamMetadata(
    string Codec,
    int Width,
    int Height,
    double FramesPerSecond)
{
    public bool NeedsOptimization(int maximumWidth, int maximumHeight)
        => Width > maximumWidth ||
           Height > maximumHeight ||
           FramesPerSecond > VideoProxyCache.MaximumFramesPerSecond + 0.05 ||
           !VideoProxyCache.IsEfficientPlaybackCodec(Codec);
}

/// <summary>
/// Creates display-sized H.264 copies next to managed source videos. The
/// original import remains untouched and every conversion is written through
/// a temporary file so an interrupted conversion cannot become playable.
/// </summary>
public sealed class VideoProxyCache
{
    public const int MaximumFramesPerSecond = 30;
    private const string CacheDirectoryName = ".vellume-cache";
    private const string ProxyRevision = "v2";
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;

    public VideoProxyCache(
        string? ffmpegPath = null,
        string? ffprobePath = null)
    {
        _ffmpegPath = ResolveToolPath(
            ffmpegPath,
            "VELLUME_FFMPEG_PATH",
            "ffmpeg.exe");
        _ffprobePath = ResolveToolPath(
            ffprobePath,
            "VELLUME_FFPROBE_PATH",
            "ffprobe.exe");
    }

    public bool IsAvailable =>
        _ffmpegPath is not null && _ffprobePath is not null;

    public async Task<VideoPlaybackPreparation> PrepareAsync(
        WallpaperItem wallpaper,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        var playback = wallpaper.Copy();
        if (wallpaper.Kind != WallpaperKind.Video ||
            !wallpaper.IsManaged ||
            !File.Exists(wallpaper.SourcePath) ||
            maximumWidth < 320 ||
            maximumHeight < 180)
        {
            return Original(playback, "최적화 대상이 아닌 월페이퍼입니다.");
        }

        var sourcePath = Path.GetFullPath(wallpaper.SourcePath);
        var itemDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(itemDirectory))
        {
            return Original(playback, "관리형 영상 폴더를 찾지 못했습니다.");
        }

        maximumWidth = MakeEven(maximumWidth);
        maximumHeight = MakeEven(maximumHeight);
        var cacheDirectory = Path.Combine(
            itemDirectory,
            CacheDirectoryName);
        var proxyPath = Path.Combine(
            cacheDirectory,
            $"video-{maximumWidth}x{maximumHeight}-{MaximumFramesPerSecond}fps-h264-{ProxyRevision}.mp4");
        if (IsFreshProxy(sourcePath, proxyPath))
        {
            playback.SourcePath = proxyPath;
            return new(
                playback,
                UsedProxy: true,
                GeneratedProxy: false,
                $"{maximumWidth}×{maximumHeight} 최적화 캐시를 사용합니다.");
        }

        if (!IsAvailable)
        {
            return Original(
                playback,
                "FFmpeg 도구를 찾지 못해 원본 영상을 사용합니다.");
        }

        VideoStreamMetadata? metadata;
        try
        {
            metadata = await ProbeAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Original(
                playback,
                "영상 정보를 읽지 못해 원본 영상을 사용합니다.");
        }
        if (metadata is null ||
            !metadata.NeedsOptimization(maximumWidth, maximumHeight))
        {
            return Original(
                playback,
                metadata is null
                    ? "영상 정보를 읽지 못해 원본 영상을 사용합니다."
                    : "원본이 이미 디스플레이에 적합합니다.");
        }

        var temporaryPath = Path.Combine(
            cacheDirectory,
            $".video-{Guid.NewGuid():N}.partial.mp4");
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var targetFrameRate = Math.Clamp(
                metadata.FramesPerSecond <= 0
                    ? MaximumFramesPerSecond
                    : metadata.FramesPerSecond,
                1,
                MaximumFramesPerSecond);
            var converted = await TryConvertWithNvencAsync(
                    sourcePath,
                    temporaryPath,
                    maximumWidth,
                    maximumHeight,
                    targetFrameRate,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!converted)
            {
                TryDeleteFile(temporaryPath);
                converted = await TryConvertWithSoftwareAsync(
                        sourcePath,
                        temporaryPath,
                        maximumWidth,
                        maximumHeight,
                        targetFrameRate,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!converted ||
                !File.Exists(temporaryPath) ||
                new FileInfo(temporaryPath).Length == 0)
            {
                TryDeleteFile(temporaryPath);
                return Original(
                    playback,
                    "영상 프록시 생성에 실패해 원본 영상을 사용합니다.");
            }

            File.Move(temporaryPath, proxyPath, overwrite: true);
            DeleteSupersededProxies(cacheDirectory, proxyPath);
            playback.SourcePath = proxyPath;
            return new(
                playback,
                UsedProxy: true,
                GeneratedProxy: true,
                $"{metadata.Width}×{metadata.Height} {metadata.Codec} 영상을 " +
                $"{maximumWidth}×{maximumHeight} H.264 재생 캐시로 최적화했습니다.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            return Original(
                playback,
                "영상 최적화 중 오류가 발생해 원본 영상을 사용합니다.");
        }
    }

    public static bool IsEfficientPlaybackCodec(string? codec)
        => codec?.Trim().ToLowerInvariant() is "h264" or "hevc";

    private async Task<VideoStreamMetadata?> ProbeAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
                _ffprobePath!,
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=codec_name,width,height,avg_frame_rate",
                    "-of", "json",
                    sourcePath
                ],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() == 0)
            {
                return null;
            }

            var stream = streams[0];
            var codec = stream.GetProperty("codec_name").GetString() ??
                        string.Empty;
            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();
            var rate = stream.TryGetProperty("avg_frame_rate", out var rateNode)
                ? ParseFrameRate(rateNode.GetString())
                : 0;
            return width > 0 && height > 0
                ? new VideoStreamMetadata(codec, width, height, rate)
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or
            KeyNotFoundException or
            InvalidOperationException or
            FormatException)
        {
            return null;
        }
    }

    private Task<bool> TryConvertWithNvencAsync(
        string sourcePath,
        string outputPath,
        int maximumWidth,
        int maximumHeight,
        double frameRate,
        CancellationToken cancellationToken)
    {
        var filter =
            $"scale_cuda={maximumWidth}:{maximumHeight}:" +
            "force_original_aspect_ratio=decrease:force_divisible_by=2";
        return RunConversionAsync(
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-hwaccel", "cuda",
                "-hwaccel_output_format", "cuda",
                "-i", sourcePath,
                "-map", "0:v:0",
                "-map", "0:a?",
                "-vf", filter,
                "-r", FormatFrameRate(frameRate),
                "-c:v", "h264_nvenc",
                "-preset", "p5",
                "-tune", "hq",
                "-rc", "vbr",
                "-cq", "22",
                "-b:v", "5M",
                "-maxrate", "8M",
                "-bufsize", "16M",
                "-c:a", "aac",
                "-b:a", "128k",
                "-movflags", "+faststart",
                outputPath
            ],
            cancellationToken);
    }

    private Task<bool> TryConvertWithSoftwareAsync(
        string sourcePath,
        string outputPath,
        int maximumWidth,
        int maximumHeight,
        double frameRate,
        CancellationToken cancellationToken)
    {
        var filter =
            $"scale={maximumWidth}:{maximumHeight}:" +
            "force_original_aspect_ratio=decrease:force_divisible_by=2," +
            "format=yuv420p";
        return RunConversionAsync(
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", sourcePath,
                "-map", "0:v:0",
                "-map", "0:a?",
                "-vf", filter,
                "-r", FormatFrameRate(frameRate),
                "-c:v", "libx264",
                "-preset", "fast",
                "-crf", "22",
                "-c:a", "aac",
                "-b:a", "128k",
                "-movflags", "+faststart",
                outputPath
            ],
            cancellationToken);
    }

    private async Task<bool> RunConversionAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
                _ffmpegPath!,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new ProcessResult(-1, string.Empty, "프로세스를 시작하지 못했습니다.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
    }

    private static string? ResolveToolPath(
        string? explicitPath,
        string environmentVariable,
        string executableName)
    {
        foreach (var candidate in new[]
                 {
                     explicitPath,
                     Environment.GetEnvironmentVariable(environmentVariable),
                     Path.Combine(AppContext.BaseDirectory, executableName)
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(
                    directory.Trim('"'),
                    executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries and keep looking.
            }
        }

        return null;
    }

    private static bool IsFreshProxy(
        string sourcePath,
        string proxyPath)
    {
        if (!File.Exists(proxyPath))
        {
            return false;
        }

        var source = new FileInfo(sourcePath);
        var proxy = new FileInfo(proxyPath);
        return proxy.Length > 0 &&
               proxy.LastWriteTimeUtc >= source.LastWriteTimeUtc;
    }

    private static int MakeEven(int value)
        => Math.Max(2, value - Math.Abs(value % 2));

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split('/', 2);
        if (!double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numerator))
        {
            return 0;
        }
        if (parts.Length == 1)
        {
            return numerator;
        }
        return double.TryParse(
                   parts[1],
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var denominator) &&
               denominator > 0
            ? numerator / denominator
            : 0;
    }

    private static string FormatFrameRate(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static VideoPlaybackPreparation Original(
        WallpaperItem wallpaper,
        string detail)
        => new(
            wallpaper,
            UsedProxy: false,
            GeneratedProxy: false,
            detail);

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Cancellation must not be hidden by cleanup failures.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The next preparation attempt uses a new temporary name.
        }
    }

    private static void DeleteSupersededProxies(
        string cacheDirectory,
        string retainedPath)
    {
        try
        {
            var retainedName = Path.GetFileNameWithoutExtension(retainedPath);
            var revisionSeparator = retainedName.LastIndexOf(
                "-v",
                StringComparison.OrdinalIgnoreCase);
            if (revisionSeparator < 0)
            {
                return;
            }
            var familyPattern =
                retainedName[..revisionSeparator] + "-v*.mp4";
            foreach (var file in Directory.EnumerateFiles(
                         cacheDirectory,
                         familyPattern,
                         SearchOption.TopDirectoryOnly))
            {
                if (!Path.GetFullPath(file).Equals(
                        Path.GetFullPath(retainedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch
        {
            // Old caches are harmless and can be removed by a later run.
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Output,
        string Error);
}
