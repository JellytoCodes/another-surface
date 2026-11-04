using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class WindowsStartupRegistrationServiceTests
{
    [Fact]
    public void BuildCommand_QuotesExecutableAndUsesBackgroundStartupArgument()
    {
        var command = WindowsStartupRegistrationService.BuildCommand(
            @"C:\Program Files\AnotherSurface\AnotherSurface.exe");

        Assert.Equal(
            "\"C:\\Program Files\\AnotherSurface\\AnotherSurface.exe\" --windows-startup",
            command);
    }

    [Fact]
    public void EnableAndDisable_UpdatesOnlyOurRegistrationAndRemovesLegacyName()
    {
        var path = @"Software\AnotherSurface.Tests\Startup-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("Vellume", "old path");
                key.SetValue("OtherApp", "untouched");
            }
            var service = new WindowsStartupRegistrationService(Environment.ProcessPath, path);
            service.SetEnabled(true);
            Assert.True(service.IsEnabled());
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)!)
            {
                Assert.Null(key.GetValue("Vellume"));
                Assert.Equal("untouched", key.GetValue("OtherApp"));
                Assert.Equal(WindowsStartupRegistrationService.BuildCommand(Environment.ProcessPath!), key.GetValue("Another Surface"));
            }
            service.SetEnabled(false);
            Assert.False(service.IsEnabled());
            using var after = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)!;
            Assert.Equal("untouched", after.GetValue("OtherApp"));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Enable_RejectsMissingExecutableBeforeWritingRegistration()
    {
        var path = @"Software\AnotherSurface.Tests\Startup-" + Guid.NewGuid().ToString("N");
        var service = new WindowsStartupRegistrationService(@"Z:\Missing\AnotherSurface.exe", path);
        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true));
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
        Assert.Null(key);
    }
}
