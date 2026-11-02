using System.IO;
using Microsoft.Win32;

namespace AnotherSurface.Windows;

public sealed class WindowsStartupRegistrationService
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Another Surface";
    private const string LegacyValueName = "Vellume";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: false);
        return (key?.GetValue(ValueName) ?? key?.GetValue(LegacyValueName)) is string command &&
               !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            RunKeyPath,
            writable: true) ?? throw new InvalidOperationException(
            "Windows 자동 실행 레지스트리를 열 수 없습니다.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !executablePath.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Another Surface 실행 파일 경로를 확인할 수 없습니다.");
        }

        // HKCU Run is per-user, needs no administrator permission and starts
        // Another Surface only after this Windows account signs in.
        key.SetValue(
            ValueName,
            BuildCommand(executablePath),
            RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    internal static string BuildCommand(string executablePath)
        => $"\"{Path.GetFullPath(executablePath)}\" --windows-startup";
}
