using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnotherSurface.Core;

public sealed record VideoPlaybackPreparation(
    WallpaperItem Wallpaper,
    bool UsedProxy,
    bool GeneratedProxy,
    string Detail);

public sealed record VideoCacheCleanupResult(
    int RemovedFiles,
    long RemovedBytes,
    long RetainedBytes);

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
    public const long DefaultMaximumCacheBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan DefaultUnusedCacheAge = TimeSpan.FromDays(30);
    private const string CacheDirectoryName = ".another-surface-cache";
    private const string ProxyRevision = "v2";
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;

    public VideoProxyCache(
        string? ffmpegPath = null,
        string? ffprobePath = null)
    {
        _ffmpegPath = ResolveToolPath(
            ffmpegPath,
            "ANOTHER_SURFACE_FFMPEG_PATH",
            "ffmpeg.exe");
        _ffprobePath = ResolveToolPath(
            ffprobePath,
            "ANOTHER_SURFACE_FFPROBE_PATH",
            "ffprobe.exe");
    }

    public bool IsAvailable =>
        _ffmpegPath is not null && _ffprobePath is not null;

    public async Task<double> GetDurationAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (_ffprobePath is null)
            throw new InvalidOperationException("영상 길이를 확인하려면 기존 영상 최적화 도구인 ffprobe가 필요합니다. PATH 또는 ANOTHER_SURFACE_FFPROBE_PATH를 확인하세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await RunProcessAsync(_ffprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", sourcePath],
            timeout.Token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !double.TryParse(result.Output.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("영상의 전체 길이를 읽지 못했습니다.");
        return duration;
    }

    public async Task CreateVideoThumbnailAsync(string sourcePath, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (_ffmpegPath is null)
            throw new InvalidOperationException("영상 미리보기를 만들려면 ffmpeg가 필요합니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var seek = _ffprobePath is null ? 0
            : Math.Min(1, await GetDurationAsync(sourcePath, timeout.Token).ConfigureAwait(false) * 0.1);
        var result = await RunProcessAsync(_ffmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-ss", seek.ToString("R", CultureInfo.InvariantCulture), "-i", sourcePath,
             "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=320:180:force_original_aspect_ratio=decrease",
             "-threads", "1", outputPath], timeout.Token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("영상 미리보기를 만들지 못했습니다.");
    }

    internal async Task<VideoPlaybackPreparation> PrepareSegmentAsync(WallpaperItem original,
        int maximumWidth, int maximumHeight, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("구간 재생에는 ffmpeg와 ffprobe가 필요합니다. 기존 영상 최적화 도구 경로를 확인하세요.");
        var duration = await GetDurationAsync(original.SourcePath, cancellationToken).ConfigureAwait(false);
        original.ValidateVideoRange(duration);
        var start = original.VideoStartSeconds!.Value;
        var end = original.VideoEndSeconds!.Value;
        var input = new FileInfo(original.SourcePath);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"{input.FullName}|{input.Length}|{input.LastWriteTimeUtc.Ticks}|{start:R}|{end:R}|{maximumWidth}x{maximumHeight}")))).ToLowerInvariant();
        var directory = Path.Combine(Path.GetDirectoryName(original.SourcePath)!, CacheDirectoryName);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"segment-{key}-v1.mp4");
        var playback = original.Copy();
        playback.SourcePath = target;
        // The renderer loops a complete cached clip; do not apply the original offsets again.
        playback.VideoStartSeconds = null;
        playback.VideoEndSeconds = null;
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            TouchProxy(target);
            return new(playback, true, false, "저장된 구간 재생 캐시를 사용합니다.");
        }
        var temporary = Path.Combine(directory, $".segment-{Guid.NewGuid():N}.partial.mp4");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var filter = "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p";
            if (maximumWidth > 0 && maximumHeight > 0)
            {
                var metadata = await ProbeAsync(input.FullName, timeout.Token).ConfigureAwait(false);
                var rate = Math.Clamp(metadata?.FramesPerSecond is > 0 ? metadata.FramesPerSecond : 30, 1, 30);
                filter = $"scale=w='min(iw,{MakeEven(maximumWidth)})':h='min(ih,{MakeEven(maximumHeight)})':" +
                    $"force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p,fps={FormatFrameRate(rate)}";
            }
            var result = await RunProcessAsync(_ffmpegPath!,
                ["-y", "-hide_banner", "-loglevel", "error",
                 "-ss", start.ToString("R", CultureInfo.InvariantCulture), "-i", input.FullName,
                 "-t", (end - start).ToString("R", CultureInfo.InvariantCulture),
                 "-map", "0:v:0", "-map", "0:a?", "-vf", filter,
                 "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-threads", "2",
                 "-c:a", "aac", "-movflags", "+faststart", temporary], timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidOperationException("구간 재생 캐시 생성에 실패했습니다. 원본은 변경되지 않았습니다.");
            File.Move(temporary, target, overwrite: true);
            TouchProxy(target);
            return new(playback, true, true, "선택한 시간 구간의 반복 재생 캐시를 만들었습니다.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("구간 캐시 생성이 2분을 초과했습니다. 더 짧은 구간을 선택하거나 영상 최적화를 켜주세요.");
        }
        finally { TryDeleteFile(temporary); }
    }

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
            TouchProxy(proxyPath);
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
            TouchProxy(proxyPath);
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

    private static void TouchProxy(string path)
    {
        try
        {
            // Windows may disable automatic access timestamps; track reuse explicitly.
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache accounting must never prevent playback.
        }
    }

    internal static VideoCacheCleanupResult Cleanup(
        string libraryDirectory,
        IReadOnlyCollection<string> protectedPaths,
        long maximumBytes,
        TimeSpan maximumUnusedAge,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (maximumUnusedAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUnusedAge));
        }
        var root = Path.GetFullPath(libraryDirectory);
        var protectedFiles = protectedPaths.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<FileInfo>();
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return new(0, 0, 0);
        }
        foreach (var itemDirectory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(itemDirectory), "N", out _) ||
                IsReparsePoint(itemDirectory))
            {
                continue;
            }
            var cacheDirectory = Path.Combine(itemDirectory, CacheDirectoryName);
            if (!Directory.Exists(cacheDirectory) || IsReparsePoint(cacheDirectory))
            {
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.mp4"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only generated complete proxies, never original media, partial files,
                // arbitrary nested content or linked files/directories.
                if (Regex.IsMatch(Path.GetFileName(path),
                        @"^(video-\d+x\d+-\d+fps-h264-v\d+|segment-[a-f0-9]{64}-v1)\.mp4$",
                        RegexOptions.CultureInvariant) && !IsReparsePoint(path))
                {
                    candidates.Add(new FileInfo(path));
                }
            }
        }
        var retainedBytes = candidates.Sum(file => file.Length);
        var cutoff = DateTime.UtcNow - maximumUnusedAge;
        var removedFiles = 0;
        long removedBytes = 0;
        foreach (var file in candidates.OrderBy(file =>
                     file.LastAccessTimeUtc > file.LastWriteTimeUtc
                         ? file.LastAccessTimeUtc : file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastUsed = file.LastAccessTimeUtc > file.LastWriteTimeUtc
                ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;
            if (protectedFiles.Contains(file.FullName) ||
                (retainedBytes <= maximumBytes && lastUsed >= cutoff))
            {
                continue;
            }
            try
            {
                // Recheck links at the mutation boundary. Delete only this exact file.
                if (IsReparsePoint(root) || IsReparsePoint(file.Directory!.Parent!.FullName) ||
                    IsReparsePoint(file.Directory.FullName) || IsReparsePoint(file.FullName))
                {
                    continue;
                }
                var length = file.Length;
                File.Delete(file.FullName);
                retainedBytes -= length;
                removedBytes += length;
                removedFiles++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A renderer or encoder may still own the file. Leave it for a later run.
            }
        }
        return new(removedFiles, removedBytes, retainedBytes);
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private sealed record ProcessResult(
        int ExitCode,
        string Output,
        string Error);
}
