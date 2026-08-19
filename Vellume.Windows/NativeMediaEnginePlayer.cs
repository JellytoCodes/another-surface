using System.IO;
using System.Runtime.InteropServices;

namespace Vellume.Windows;

/// <summary>
/// Plays local video directly into an HWND through Windows Media Foundation.
/// This avoids creating a Chromium compositor for ordinary MP4 wallpaper.
/// </summary>
internal sealed class NativeMediaEnginePlayer : IDisposable
{
    private IMFMediaEngine? _engine;
    private MediaEngineNotify? _notify;
    private bool _runtimeAcquired;
    private bool _disposed;

    internal bool IsReady { get; private set; }
    internal uint NativeWidth { get; private set; }
    internal uint NativeHeight { get; private set; }

    internal async Task InitializeAsync(
        nint playbackWindow,
        string sourcePath,
        bool isMuted,
        double volume,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (playbackWindow == nint.Zero ||
            !NativeMethods.IsWindow(playbackWindow))
        {
            throw new InvalidOperationException(
                "Media Foundation 영상 출력 창이 준비되지 않았습니다.");
        }

        MediaFoundationRuntime.Acquire();
        _runtimeAcquired = true;

        IMFAttributes? attributes = null;
        IMFMediaEngineClassFactory? factory = null;
        try
        {
            ThrowIfFailed(MediaFoundationNative.MFCreateAttributes(
                out attributes,
                2));

            _notify = new MediaEngineNotify();
            var callbackKey =
                MediaFoundationNative.MediaEngineCallback;
            ThrowIfFailed(attributes.SetUnknown(
                ref callbackKey,
                _notify));
            var playbackWindowKey =
                MediaFoundationNative.MediaEnginePlaybackHwnd;
            ThrowIfFailed(attributes.SetUINT64(
                ref playbackWindowKey,
                unchecked((ulong)playbackWindow.ToInt64())));

            var factoryType = Type.GetTypeFromCLSID(
                MediaFoundationNative.MediaEngineClassFactory,
                throwOnError: true) ??
                throw new InvalidOperationException(
                    "Media Foundation Media Engine 팩터리를 찾지 못했습니다.");
            factory = (IMFMediaEngineClassFactory)(
                Activator.CreateInstance(factoryType) ??
                throw new InvalidOperationException(
                    "Media Foundation Media Engine 팩터리를 만들지 못했습니다."));
            ThrowIfFailed(factory.CreateInstance(
                0,
                attributes,
                out _engine));

            ThrowIfFailed(_engine.SetLoop(true));
            // Start explicitly after metadata is ready so the renderer does
            // not expose an undecoded black frame during initial handoff.
            ThrowIfFailed(_engine.SetAutoPlay(false));
            ThrowIfFailed(_engine.SetMuted(isMuted));
            ThrowIfFailed(_engine.SetVolume(
                Math.Clamp(volume, 0, 1)));

            // The desktop Media Foundation resolver accepts a local Windows
            // path directly. A percent-escaped file URI can fail for Korean
            // or other non-ASCII file names with ERROR_FILE_NOT_FOUND.
            ThrowIfFailed(_engine.SetSource(
                Path.GetFullPath(sourcePath)));
            ThrowIfFailed(_engine.Load());

            await _notify.WaitUntilMetadataReadyAsync(
                cancellationToken);
            ThrowIfFailed(_engine.GetNativeVideoSize(
                out var nativeWidth,
                out var nativeHeight));
            NativeWidth = nativeWidth;
            NativeHeight = nativeHeight;
            if (NativeWidth == 0 || NativeHeight == 0)
            {
                throw new InvalidOperationException(
                    "Media Foundation이 영상 크기를 확인하지 못했습니다.");
            }

            ThrowIfFailed(_engine.Play());
            // Keep the Windows-owned safety frame until Media Engine has
            // actually presented a decoded frame.
            await _notify.WaitUntilFirstFrameReadyAsync(
                cancellationToken);
            IsReady = true;
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            ReleaseComObject(factory);
            ReleaseComObject(attributes);
        }
    }

    internal Task PauseAsync()
    {
        if (_engine is not null)
        {
            ThrowIfFailed(_engine.Pause());
        }

        return Task.CompletedTask;
    }

    internal Task ResumeAsync()
    {
        if (_engine is not null)
        {
            ThrowIfFailed(_engine.Play());
        }

        return Task.CompletedTask;
    }

    internal Task UpdateAudioAsync(bool isMuted, double volume)
    {
        if (_engine is not null)
        {
            ThrowIfFailed(_engine.SetMuted(isMuted));
            ThrowIfFailed(_engine.SetVolume(
                Math.Clamp(volume, 0, 1)));
        }

        return Task.CompletedTask;
    }

    internal void UpdateVideoLayout(
        int x,
        int y,
        int width,
        int height)
    {
        if (_engine is null ||
            _disposed ||
            width <= 0 ||
            height <= 0)
        {
            return;
        }

        var engineEx = _engine as IMFMediaEngineEx ??
            throw new InvalidOperationException(
                "Media Foundation extended video output is unavailable.");
        var destination = new MediaFoundationRect
        {
            Left = x,
            Top = y,
            Right = x + width,
            Bottom = y + height
        };
        var border = new MediaFoundationArgb
        {
            Alpha = byte.MaxValue,
            Red = 0,
            Green = 0,
            Blue = 0
        };
        ThrowIfFailed(engineEx.UpdateVideoStream(
            nint.Zero,
            ref destination,
            ref border));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;
        _notify?.Cancel();
        _notify = null;
        if (_engine is not null)
        {
            try
            {
                _engine.Pause();
                _engine.Shutdown();
            }
            catch
            {
                // Native shutdown continues by releasing the COM object.
            }
        }

        ReleaseComObject(_engine);
        _engine = null;
        if (_runtimeAcquired)
        {
            _runtimeAcquired = false;
            MediaFoundationRuntime.Release();
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal static class MediaFoundationRuntime
{
    private static readonly object Sync = new();
    private static int _referenceCount;

    internal static void Acquire()
    {
        lock (Sync)
        {
            if (_referenceCount == 0)
            {
                var result = MediaFoundationNative.MFStartup(
                    MediaFoundationNative.Version,
                    0);
                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }
            }

            _referenceCount++;
        }
    }

    internal static void Release()
    {
        lock (Sync)
        {
            if (_referenceCount == 0)
            {
                return;
            }

            _referenceCount--;
            if (_referenceCount == 0)
            {
                MediaFoundationNative.MFShutdown();
            }
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class MediaEngineNotify : IMFMediaEngineNotify
{
    private readonly TaskCompletionSource<bool> _metadataReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _firstFrameReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int EventNotify(
        uint eventId,
        nuint parameterOne,
        uint parameterTwo)
    {
        switch (eventId)
        {
            case MediaFoundationNative.EventLoadedData:
            case MediaFoundationNative.EventCanPlay:
                _metadataReady.TrySetResult(true);
                break;
            case MediaFoundationNative.EventFirstFrameReady:
                _firstFrameReady.TrySetResult(true);
                break;
            case MediaFoundationNative.EventError:
            case MediaFoundationNative.EventStreamRenderingError:
                var exception = new InvalidOperationException(
                    "Media Foundation이 영상을 디코딩하지 못했습니다 " +
                    $"(0x{parameterOne:X}, 0x{parameterTwo:X8}).");
                _metadataReady.TrySetException(exception);
                _firstFrameReady.TrySetException(exception);
                break;
        }

        return 0;
    }

    internal async Task WaitUntilMetadataReadyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _metadataReady.Task.WaitAsync(
                TimeSpan.FromSeconds(8),
                cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "Media Foundation 영상이 8초 안에 준비되지 않았습니다.",
                exception);
        }
    }

    internal async Task WaitUntilFirstFrameReadyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _firstFrameReady.Task.WaitAsync(
                TimeSpan.FromSeconds(8),
                cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "Media Foundation의 첫 영상 프레임이 8초 안에 표시되지 않았습니다.",
                exception);
        }
    }

    internal void Cancel()
    {
        _metadataReady.TrySetCanceled();
        _firstFrameReady.TrySetCanceled();
    }
}

internal static class MediaFoundationNative
{
    internal const int Version = 0x00020070;
    internal const uint EventError = 5;
    internal const uint EventLoadedData = 11;
    internal const uint EventCanPlay = 14;
    internal const uint EventFirstFrameReady = 1009;
    internal const uint EventStreamRenderingError = 1014;

    internal static readonly Guid MediaEngineClassFactory =
        new("B44392DA-499B-446B-A4CB-005FEAD0E6D5");
    internal static readonly Guid MediaEngineCallback =
        new("C60381B8-83A4-41F8-A3D0-DE05076849A9");
    internal static readonly Guid MediaEnginePlaybackHwnd =
        new("D988879B-67C9-4D92-BAA7-6EADD446039D");

    // Starts the Windows Media Foundation platform for native video playback.
    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(int version, int flags);

    // Releases the Media Foundation platform after the last native player.
    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    // Creates the attribute store used to bind Media Engine to an HWND.
    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(
        out IMFAttributes attributes,
        uint initialSize);
}

[ComImport]
[Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineNotify
{
    [PreserveSig]
    int EventNotify(
        uint eventId,
        nuint parameterOne,
        uint parameterTwo);
}

[ComImport]
[Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineClassFactory
{
    [PreserveSig]
    int CreateInstance(
        uint flags,
        IMFAttributes attributes,
        out IMFMediaEngine mediaEngine);

    [PreserveSig]
    int CreateTimeRange(out nint timeRange);

    [PreserveSig]
    int CreateError(out nint error);
}

[ComImport]
[Guid("98A1B0BB-03EB-4935-AE7C-93C1FA0E1C93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngine
{
    [PreserveSig] int GetError(out nint error);
    [PreserveSig] int SetErrorCode(int error);
    [PreserveSig] int SetSourceElements(nint sourceElements);
    [PreserveSig] int SetSource(
        [MarshalAs(UnmanagedType.BStr)] string source);
    [PreserveSig] int GetCurrentSource(
        [MarshalAs(UnmanagedType.BStr)] out string source);
    [PreserveSig] ushort GetNetworkState();
    [PreserveSig] int GetPreload();
    [PreserveSig] int SetPreload(int preload);
    [PreserveSig] int GetBuffered(out nint buffered);
    [PreserveSig] int Load();
    [PreserveSig] int CanPlayType(
        [MarshalAs(UnmanagedType.BStr)] string type,
        out int answer);
    [PreserveSig] ushort GetReadyState();
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsSeeking();
    [PreserveSig] double GetCurrentTime();
    [PreserveSig] int SetCurrentTime(double seekTime);
    [PreserveSig] double GetStartTime();
    [PreserveSig] double GetDuration();
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsPaused();
    [PreserveSig] double GetDefaultPlaybackRate();
    [PreserveSig] int SetDefaultPlaybackRate(double rate);
    [PreserveSig] double GetPlaybackRate();
    [PreserveSig] int SetPlaybackRate(double rate);
    [PreserveSig] int GetPlayed(out nint played);
    [PreserveSig] int GetSeekable(out nint seekable);
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsEnded();
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetAutoPlay();
    [PreserveSig] int SetAutoPlay(
        [MarshalAs(UnmanagedType.Bool)] bool autoPlay);
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetLoop();
    [PreserveSig] int SetLoop(
        [MarshalAs(UnmanagedType.Bool)] bool loop);
    [PreserveSig] int Play();
    [PreserveSig] int Pause();
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetMuted();
    [PreserveSig] int SetMuted(
        [MarshalAs(UnmanagedType.Bool)] bool muted);
    [PreserveSig] double GetVolume();
    [PreserveSig] int SetVolume(double volume);
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool HasVideo();
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool HasAudio();
    [PreserveSig] int GetNativeVideoSize(
        out uint width,
        out uint height);
    [PreserveSig] int GetVideoAspectRatio(
        out uint width,
        out uint height);
    [PreserveSig] int Shutdown();
    [PreserveSig] int TransferVideoFrame(
        nint destinationSurface,
        nint sourceRectangle,
        nint destinationRectangle,
        nint borderColor);
    [PreserveSig] int OnVideoStreamTick(out long presentationTime);
}

[ComImport]
[Guid("83015EAD-B1E6-40D0-A98A-37145FFE1AD1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineEx : IMFMediaEngine
{
    [PreserveSig]
    int SetSourceFromByteStream(
        nint byteStream,
        [MarshalAs(UnmanagedType.BStr)] string url);

    [PreserveSig]
    int GetStatistics(
        int statisticId,
        nint statistic);

    [PreserveSig]
    int UpdateVideoStream(
        nint sourceRectangle,
        ref MediaFoundationRect destinationRectangle,
        ref MediaFoundationArgb borderColor);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MediaFoundationRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MediaFoundationArgb
{
    internal byte Blue;
    internal byte Green;
    internal byte Red;
    internal byte Alpha;
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, nint value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(
        ref Guid key,
        nint value,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(
        IMFAttributes attributes,
        int matchType,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out uint length);
    [PreserveSig] int GetString(
        ref Guid key,
        nint value,
        uint bufferSize,
        nint length);
    [PreserveSig] int GetAllocatedString(
        ref Guid key,
        out nint value,
        out uint length);
    [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
    [PreserveSig] int GetBlob(
        ref Guid key,
        nint buffer,
        uint bufferSize,
        nint blobSize);
    [PreserveSig] int GetAllocatedBlob(
        ref Guid key,
        out nint buffer,
        out uint size);
    [PreserveSig] int GetUnknown(
        ref Guid key,
        ref Guid interfaceId,
        out nint value);
    [PreserveSig] int SetItem(ref Guid key, nint value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(
        ref Guid key,
        [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(
        ref Guid key,
        nint buffer,
        uint bufferSize);
    [PreserveSig] int SetUnknown(
        ref Guid key,
        [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint itemCount);
    [PreserveSig] int GetItemByIndex(
        uint index,
        out Guid key,
        nint value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}
