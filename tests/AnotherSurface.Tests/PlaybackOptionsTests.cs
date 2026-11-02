using AnotherSurface.Core;
using System.Text.Json;

namespace AnotherSurface.Tests;

public sealed class PlaybackOptionsTests
{
    [Fact]
    public void Copy_ClampsVisualAdjustments()
    {
        var options = new PlaybackOptions
        {
            Brightness = -1,
            Contrast = 4,
            Saturation = 2
        };

        var copy = options.Copy();

        Assert.Equal(0.25, copy.Brightness);
        Assert.Equal(1.5, copy.Contrast);
        Assert.Equal(1.5, copy.Saturation);
    }

    [Fact]
    public void Defaults_PreserveOriginalWallpaperColors()
    {
        var options = new PlaybackOptions();

        Assert.Equal(1, options.Brightness);
        Assert.Equal(1, options.Contrast);
        Assert.Equal(1, options.Saturation);
        Assert.True(options.UseOptimizedVideo);
        Assert.False(options.PauseWhenForegroundAppIsMaximized);
    }

    [Fact]
    public void Defaults_DoNotSpecialCaseAnyApplication()
    {
        var options = new PlaybackOptions();

        Assert.Empty(options.ApplicationRules);
    }

    [Fact]
    public void Copy_PreservesForegroundGpuSaverPreference()
    {
        var options = new PlaybackOptions
        {
            PauseWhenForegroundAppIsMaximized = true
        };

        Assert.True(
            options.Copy().PauseWhenForegroundAppIsMaximized);
    }

    [Fact]
    public void Copy_PreservesVideoOptimizationPreference()
    {
        var options = new PlaybackOptions
        {
            UseOptimizedVideo = false
        };

        Assert.False(options.Copy().UseOptimizedVideo);
    }

    [Fact]
    public void OlderJson_DefaultsToVideoOptimizationEnabled()
    {
        var options = JsonSerializer.Deserialize<PlaybackOptions>("{}");

        Assert.NotNull(options);
        Assert.True(options.UseOptimizedVideo);
    }

    [Fact]
    public void Copy_DeepCopiesApplicationPlaybackRules()
    {
        var options = new PlaybackOptions
        {
            ApplicationRules =
            [
                new ApplicationPlaybackRule
                {
                    ProcessName = "  ExampleGame  ",
                    Condition =
                        ApplicationRuleCondition.IsForeground,
                    Action = ApplicationPlaybackAction.Pause
                }
            ]
        };

        var copy = options.Copy();

        Assert.NotSame(
            options.ApplicationRules,
            copy.ApplicationRules);
        var copiedRule = Assert.Single(copy.ApplicationRules);
        Assert.NotSame(options.ApplicationRules[0], copiedRule);
        Assert.Equal("ExampleGame", copiedRule.ProcessName);
        Assert.Equal(
            ApplicationRuleCondition.IsForeground,
            copiedRule.Condition);
        Assert.Equal(
            ApplicationPlaybackAction.Pause,
            copiedRule.Action);

        copiedRule.Action =
            ApplicationPlaybackAction.StopFreeMemory;

        Assert.Equal(
            ApplicationPlaybackAction.Pause,
            options.ApplicationRules[0].Action);
    }
}
