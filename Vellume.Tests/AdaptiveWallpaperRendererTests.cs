using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class AdaptiveWallpaperRendererTests
{
    [Theory]
    [InlineData(0.8, 1.0, 1.0)]
    [InlineData(1.0, 1.2, 1.0)]
    [InlineData(1.0, 1.0, 0.5)]
    public async Task VideoColorChange_PreparesMutedHiddenReplacementBeforeClosingNative(
        double brightness, double contrast, double saturation)
    {
        var native = new FakeRenderer("native");
        var managed = new FakeRenderer("managed");
        var renderer = await CreateStartedAsync(native, managed);
        managed.OnInitialize = () =>
        {
            Assert.False(native.Closed);
            Assert.True(native.Visible);
            Assert.False(managed.Visible);
            Assert.True(managed.InitialOptions!.IsMuted);
        };

        var options = new PlaybackOptions
        {
            Brightness = brightness,
            Contrast = contrast,
            Saturation = saturation,
            IsMuted = false,
            Volume = 0.4
        };
        await renderer.UpdatePlaybackAsync(options);

        Assert.Equal("managed", renderer.BackendDisplayName);
        Assert.True(native.Closed);
        Assert.True(native.Paused);
        Assert.True(managed.Visible);
        Assert.False(managed.Paused);
        Assert.Equal(brightness, managed.LatestOptions!.Brightness);
        Assert.Equal(contrast, managed.LatestOptions.Contrast);
        Assert.Equal(saturation, managed.LatestOptions.Saturation);
        Assert.False(managed.LatestOptions.IsMuted);
        await renderer.CloseSafelyAsync();
    }

    [Fact]
    public async Task ReturningToNeutralColors_KeepsManagedProcessUntilWallpaperReapply()
    {
        var native = new FakeRenderer("native");
        var managed = new FakeRenderer("managed");
        var nativeCreations = 0;
        var managedCreations = 0;
        var renderer = new AdaptiveWallpaperRenderer("unused",
            () => { nativeCreations++; return native; },
            () => { managedCreations++; return managed; });
        await AttachAndInitializeAsync(renderer, WallpaperKind.Video);

        await renderer.UpdatePlaybackAsync(new PlaybackOptions { Brightness = 0.8 });
        await renderer.UpdatePlaybackAsync(new PlaybackOptions());
        await renderer.UpdatePlaybackAsync(new PlaybackOptions { Saturation = 0.6 });

        Assert.Equal(1, nativeCreations);
        Assert.Equal(1, managedCreations);
        Assert.Equal("managed", renderer.BackendDisplayName);
        Assert.False(managed.Closed);
        Assert.Equal(0.6, managed.LatestOptions!.Saturation);
        await renderer.CloseSafelyAsync();
    }

    [Fact]
    public async Task ColorMigration_PreservesPausedAndHiddenState()
    {
        var native = new FakeRenderer("native");
        var managed = new FakeRenderer("managed");
        var renderer = await CreateStartedAsync(native, managed);
        await renderer.PauseAsync();
        await renderer.SetWindowVisibleAsync(false, CancellationToken.None);

        await renderer.UpdatePlaybackAsync(new PlaybackOptions { Brightness = 0.8 });

        Assert.True(managed.Paused);
        Assert.False(managed.Visible);
        Assert.Equal(0, native.ResumeCount);
        await renderer.CloseSafelyAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMigration_KeepsPreviousRendererAndResumesIfSwapPausedIt(bool failAfterPreparation)
    {
        var native = new FakeRenderer("native");
        var managed = new FakeRenderer("managed")
        {
            FailInitialization = !failAfterPreparation,
            FailUpdate = failAfterPreparation
        };
        var renderer = await CreateStartedAsync(native, managed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            renderer.UpdatePlaybackAsync(new PlaybackOptions { Contrast = 1.2 }));

        Assert.Equal("native", renderer.BackendDisplayName);
        Assert.False(native.Closed);
        Assert.False(native.Paused);
        Assert.True(managed.Closed);
        Assert.Equal(failAfterPreparation ? 1 : 0, native.ResumeCount);
        await renderer.CloseSafelyAsync();
    }

    [Theory]
    [InlineData(WallpaperKind.Image)]
    [InlineData(WallpaperKind.Video)]
    public async Task AudioAndFitUpdate_DoesNotReplaceNativeRenderer(WallpaperKind kind)
    {
        var native = new FakeRenderer("native");
        var managed = new FakeRenderer("managed");
        var renderer = await CreateStartedAsync(native, managed, kind);

        await renderer.UpdatePlaybackAsync(new PlaybackOptions
        {
            Fit = WallpaperFit.Fit,
            IsMuted = false,
            Volume = 0.5,
            Brightness = kind == WallpaperKind.Image ? 0.8 : 1
        });

        Assert.Equal("native", renderer.BackendDisplayName);
        Assert.False(native.Closed);
        Assert.Null(managed.InitialOptions);
        await renderer.CloseSafelyAsync();
    }

    private static async Task<AdaptiveWallpaperRenderer> CreateStartedAsync(
        FakeRenderer native, FakeRenderer managed, WallpaperKind kind = WallpaperKind.Video)
    {
        var renderer = new AdaptiveWallpaperRenderer("unused", () => native, () => managed);
        await AttachAndInitializeAsync(renderer, kind);
        return renderer;
    }

    private static async Task AttachAndInitializeAsync(AdaptiveWallpaperRenderer renderer, WallpaperKind kind)
    {
        await renderer.AttachToAsync(new nint(1), new DisplayInfo { Width = 1920, Height = 1080 },
            false, nint.Zero, nint.Zero, CancellationToken.None);
        await renderer.InitializeAndNavigateAsync(new WallpaperItem
        {
            Kind = kind,
            SourcePath = "test-source",
            Name = "test wallpaper"
        }, new PlaybackOptions(), CancellationToken.None);
    }

    private sealed class FakeRenderer(string name) : IWallpaperRenderer
    {
        public nint Handle => new(1);
        public string BackendDisplayName => name;
        public bool IsReady { get; private set; }
        public bool Closed { get; private set; }
        public bool Visible { get; private set; } = true;
        public bool Paused { get; private set; }
        public int ResumeCount { get; private set; }
        public bool FailInitialization { get; init; }
        public bool FailUpdate { get; init; }
        public Action? OnInitialize { get; set; }
        public PlaybackOptions? InitialOptions { get; private set; }
        public PlaybackOptions? LatestOptions { get; private set; }
        public string GetAttachmentDiagnostics() => name;
        public bool IsAttachedTo(nint hostHandle) => IsReady;
        public Task AttachToAsync(nint hostHandle, DisplayInfo display, bool raisedDesktop,
            nint shellViewHandle, nint backgroundWorkerHandle, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task InitializeAndNavigateAsync(WallpaperItem wallpaper, PlaybackOptions options,
            CancellationToken cancellationToken)
        {
            InitialOptions = options.Copy();
            OnInitialize?.Invoke();
            if (FailInitialization)
            {
                throw new InvalidOperationException("Expected initialization failure");
            }
            IsReady = true;
            return Task.CompletedTask;
        }
        public Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task SetWindowVisibleAsync(bool visible, CancellationToken cancellationToken)
        {
            Visible = visible;
            return Task.CompletedTask;
        }
        public Task PauseAsync()
        {
            Paused = true;
            return Task.CompletedTask;
        }
        public Task ResumeAsync()
        {
            Paused = false;
            ResumeCount++;
            return Task.CompletedTask;
        }
        public Task UpdatePlaybackAsync(PlaybackOptions options)
        {
            if (FailUpdate)
            {
                throw new InvalidOperationException("Expected update failure");
            }
            LatestOptions = options.Copy();
            return Task.CompletedTask;
        }
        public Task CloseSafelyAsync()
        {
            Closed = true;
            IsReady = false;
            return Task.CompletedTask;
        }
    }
}
