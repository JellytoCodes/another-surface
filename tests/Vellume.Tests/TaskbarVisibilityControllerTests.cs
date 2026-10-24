using Vellume.Windows;

namespace Vellume.Tests;

public sealed class TaskbarVisibilityControllerTests
{
    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void IsTaskbarClassName_AcceptsExplorerTaskbars(
        string className)
        => Assert.True(
            TaskbarVisibilityController.IsTaskbarClassName(
                className));

    [Theory]
    [InlineData("")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("ApplicationFrameWindow")]
    public void IsTaskbarClassName_RejectsOtherWindows(
        string className)
        => Assert.False(
            TaskbarVisibilityController.IsTaskbarClassName(
                className));
}
