using Vellume.Windows;

namespace Vellume.Tests;

public sealed class WindowsStartupRegistrationServiceTests
{
    [Fact]
    public void BuildCommand_QuotesExecutableAndUsesBackgroundStartupArgument()
    {
        var command = WindowsStartupRegistrationService.BuildCommand(
            @"C:\Program Files\Vellume\Vellume.App.exe");

        Assert.Equal(
            "\"C:\\Program Files\\Vellume\\Vellume.App.exe\" --windows-startup",
            command);
    }
}
