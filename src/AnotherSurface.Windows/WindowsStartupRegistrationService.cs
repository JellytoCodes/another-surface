using System.IO;
using Microsoft.Win32;

namespace AnotherSurface.Windows;

public sealed class WindowsStartupRegistrationService
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Another Surface";
    private const string LegacyValueName = "Vellume";
    private readonly string _runKeyPath;
    private readonly string? _executablePath;

    public WindowsStartupRegistrationService() : this(Environment.ProcessPath, RunKeyPath) { }

    internal WindowsStartupRegistrationService(string? executablePath, string runKeyPath)
    {
        _executablePath = executablePath;
        _runKeyPath = runKeyPath;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            _runKeyPath,
            writable: false);
        return (key?.GetValue(ValueName) ?? key?.GetValue(LegacyValueName)) is string command &&
               !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        var executablePath = _executablePath;
        if (enabled && (string.IsNullOrWhiteSpace(executablePath) ||
                        !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(executablePath)))
        {
            throw new InvalidOperationException("자동 실행할 Another Surface 실행 파일을 찾지 못했습니다.");
        }
        using var key = Registry.CurrentUser.CreateSubKey(
            _runKeyPath,
            writable: true) ?? throw new InvalidOperationException(
            "Windows 자동 실행 레지스트리를 열 수 없습니다.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        // HKCU Run is per-user, needs no administrator permission and starts
        // Another Surface only after this Windows account signs in.
        key.SetValue(
            ValueName,
            BuildCommand(executablePath!),
            RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (!string.Equals(key.GetValue(ValueName) as string, BuildCommand(executablePath!), StringComparison.Ordinal))
            throw new IOException("Windows 자동 실행 등록을 확인하지 못했습니다.");
    }

    internal static string BuildCommand(string executablePath)
        => $"\"{Path.GetFullPath(executablePath)}\" --windows-startup";
}
