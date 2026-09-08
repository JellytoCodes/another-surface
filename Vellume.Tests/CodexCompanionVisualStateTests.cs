using Vellume.Windows;

namespace Vellume.Tests;

public sealed class CodexCompanionVisualStateTests
{
    [Theory]
    [InlineData(
        "ready",
        "CODEX 연결됨",
        "Ready")]
    [InlineData(
        "working",
        "생각하는 중",
        "Thinking")]
    [InlineData(
        "working",
        "Vellume 빌드 중",
        "Working")]
    [InlineData(
        "complete",
        "작업 완료",
        "Complete")]
    [InlineData(
        "disconnected",
        "CODEX 연결 안 됨",
        "Offline")]
    public void ResolveVisualState_MapsHookState(
        string state,
        string headline,
        string expected)
    {
        var snapshot = new CodexCompanionSnapshot(
            state,
            headline,
            string.Empty,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            [],
            CodexUsageSnapshot.Unavailable());

        Assert.Equal(
            expected,
            NativeWidgetRendererWindow
                .ResolveCodexCompanionVisualState(snapshot)
                .ToString());
    }

    [Fact]
    public void ApplyChromaKey_RemovesOnlyDominantGreen()
    {
        byte[] pixels =
        [
            0, 255, 0, 255,
            210, 190, 80, 255,
            70, 140, 60, 255
        ];

        NativeWidgetRendererWindow.ApplyCompanionChromaKey(
            pixels);

        Assert.Equal(0, pixels[3]);
        Assert.Equal(255, pixels[7]);
        Assert.InRange(pixels[11], 1, 254);
        Assert.True(pixels[9] < 140);
    }
}
