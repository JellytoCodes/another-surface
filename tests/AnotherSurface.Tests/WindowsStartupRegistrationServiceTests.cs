using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class WindowsStartupRegistrationServiceTests
{
    [Fact]
    public void BuildCommand_QuotesExecutableAndUsesBackgroundStartupArgument()
    {
        var command = WindowsStartupRegistrationService.BuildCommand(
            @"C:\Program Files\AnotherSurface\AnotherSurface.App.exe");

        Assert.Equal(
            "\"C:\\Program Files\\AnotherSurface\\AnotherSurface.App.exe\" --windows-startup",
            command);
    }
}
