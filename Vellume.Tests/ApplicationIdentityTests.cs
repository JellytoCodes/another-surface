using Vellume.Windows;

namespace Vellume.Tests;

public sealed class ApplicationIdentityTests
{
    [Fact]
    public void AreEquivalent_CollapsesBrowserVersionDirectory()
    {
        Assert.True(ExecutablePathIdentity.AreEquivalent(
            @"C:\Program Files\Naver\Naver Whale\Application\whale.exe",
            @"C:\Program Files\Naver\Naver Whale\Application\4.38.386.14\whale.exe"));
    }

    [Fact]
    public void AreEquivalent_KeepsDifferentPackagedAppsSeparate()
    {
        Assert.False(ExecutablePathIdentity.AreEquivalent(
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__id\app\ChatGPT.exe",
            @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.0_x64__id\app\ChatGPT.exe"));
    }

    [Fact]
    public void DiscordVersionFolders_MapToOneApplicationIdentity()
    {
        Assert.True(ExecutablePathIdentity.AreEquivalent(
            @"C:\Users\User\AppData\Local\Discord\app-1.0.9249\Discord.exe",
            @"C:\Users\User\AppData\Local\Discord\app-1.0.9250\Discord.exe"));
    }

    [Theory]
    [InlineData(
        @"C:\Users\User\AppData\Local\Discord\app-1.0.9250\Discord.exe",
        true)]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", false)]
    public void KnownSingleInstanceExecutables_AreExplicit(
        string path,
        bool expected)
    {
        Assert.Equal(
            expected,
            ExecutablePathIdentity.IsKnownSingleInstanceExecutable(path));
    }
}
