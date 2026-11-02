using System.IO;

namespace AnotherSurface.App;

internal sealed record KnownAppMatch(
    string Name,
    string GroupName,
    string TargetPath);

internal static class KnownAppDiscoveryService
{
    public static IReadOnlyList<KnownAppMatch> Discover()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);

        var definitions = new[]
        {
            Define(
                "Visual Studio 2022",
                "작업",
                Path.Combine(
                    programFiles,
                    "Microsoft Visual Studio",
                    "2022",
                    "Community",
                    "Common7",
                    "IDE",
                    "devenv.exe"),
                Path.Combine(
                    programFiles,
                    "Microsoft Visual Studio",
                    "2022",
                    "Professional",
                    "Common7",
                    "IDE",
                    "devenv.exe"),
                Path.Combine(
                    programFiles,
                    "Microsoft Visual Studio",
                    "2022",
                    "Enterprise",
                    "Common7",
                    "IDE",
                    "devenv.exe"),
                Path.Combine(
                    programFiles,
                    "Microsoft Visual Studio",
                    "2022",
                    "Preview",
                    "Common7",
                    "IDE",
                    "devenv.exe")),
            Define(
                "Visual Studio Code",
                "작업",
                Path.Combine(
                    localAppData,
                    "Programs",
                    "Microsoft VS Code",
                    "Code.exe"),
                Path.Combine(
                    programFiles,
                    "Microsoft VS Code",
                    "Code.exe"),
                Path.Combine(
                    programFilesX86,
                    "Microsoft VS Code",
                    "Code.exe")),
            Define(
                "GitHub Desktop",
                "작업",
                Path.Combine(
                    localAppData,
                    "GitHubDesktop",
                    "GitHubDesktop.exe"),
                Path.Combine(
                    programFiles,
                    "GitHub Desktop",
                    "GitHubDesktop.exe")),
            Define(
                "Notion",
                "작업",
                Path.Combine(
                    localAppData,
                    "Programs",
                    "Notion",
                    "Notion.exe"),
                Path.Combine(
                    localAppData,
                    "Notion",
                    "Notion.exe")),
            Define(
                "디스코드",
                "앱",
                Path.Combine(
                    localAppData,
                    "Discord",
                    "Discord.exe"),
                Path.Combine(
                    programFiles,
                    "Discord",
                    "Discord.exe")),
            Define(
                "카카오톡",
                "앱",
                Path.Combine(
                    programFilesX86,
                    "Kakao",
                    "KakaoTalk",
                    "KakaoTalk.exe"),
                Path.Combine(
                    localAppData,
                    "Kakao",
                    "KakaoTalk",
                    "KakaoTalk.exe")),
            Define(
                "네이버 웨일",
                "앱",
                Path.Combine(
                    programFiles,
                    "Naver",
                    "Naver Whale",
                    "Application",
                    "whale.exe"),
                Path.Combine(
                    programFilesX86,
                    "Naver",
                    "Naver Whale",
                    "Application",
                    "whale.exe"),
                Path.Combine(
                    localAppData,
                    "Naver",
                    "Naver Whale",
                    "Application",
                    "whale.exe")),
            Define(
                "파이어폭스",
                "앱",
                Path.Combine(
                    programFiles,
                    "Mozilla Firefox",
                    "firefox.exe"),
                Path.Combine(
                    programFilesX86,
                    "Mozilla Firefox",
                    "firefox.exe")),
            Define(
                "에픽게임즈 런처",
                "게임",
                Path.Combine(
                    programFilesX86,
                    "Epic Games",
                    "Launcher",
                    "Portal",
                    "Binaries",
                    "Win64",
                    "EpicGamesLauncher.exe"),
                Path.Combine(
                    programFiles,
                    "Epic Games",
                    "Launcher",
                    "Portal",
                    "Binaries",
                    "Win64",
                    "EpicGamesLauncher.exe"))
        };

        var matches = new List<KnownAppMatch>();
        foreach (var definition in definitions)
        {
            var target = definition.Candidates.FirstOrDefault(File.Exists);
            target ??= definition.Name switch
            {
                "GitHub Desktop" => FindNewestSquirrelApp(
                    Path.Combine(localAppData, "GitHubDesktop"),
                    "GitHubDesktop.exe"),
                "디스코드" => FindNewestSquirrelApp(
                    Path.Combine(localAppData, "Discord"),
                    "Discord.exe"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(target))
            {
                matches.Add(new KnownAppMatch(
                    definition.Name,
                    definition.GroupName,
                    Path.GetFullPath(target)));
            }
        }

        return matches;
    }

    private static KnownAppDefinition Define(
        string name,
        string groupName,
        params string[] candidates)
        => new(name, groupName, candidates);

    private static string? FindNewestSquirrelApp(
        string root,
        string executableName)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory
                .EnumerateDirectories(
                    root,
                    "app-*",
                    SearchOption.TopDirectoryOnly)
                .Select(directory => new
                {
                    Directory = directory,
                    Executable = Path.Combine(
                        directory,
                        executableName)
                })
                .Where(item => File.Exists(item.Executable))
                .OrderByDescending(item =>
                    Directory.GetLastWriteTimeUtc(item.Directory))
                .Select(item => item.Executable)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record KnownAppDefinition(
        string Name,
        string GroupName,
        IReadOnlyList<string> Candidates);
}
