using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Vellume.Core;

namespace Vellume.Windows;

public sealed class WindowsSystemShortcutService : ISystemShortcutService
{
    private const int CancelledHResult =
        unchecked((int)0x800704C7);

    public bool RecycleBinHasItems()
    {
        var info = new RecycleBinInfo
        {
            Size = Marshal.SizeOf<RecycleBinInfo>()
        };
        return SHQueryRecycleBinW(null, ref info) >= 0 &&
               info.ItemCount > 0;
    }

    public Task LaunchAsync(
        SystemShortcutKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.Windows),
            "explorer.exe");
        var startInfo = kind switch
        {
            SystemShortcutKind.ThisPc => new ProcessStartInfo
            {
                FileName = explorerPath,
                Arguments = "shell:MyComputerFolder",
                UseShellExecute = true
            },
            SystemShortcutKind.RecycleBin => new ProcessStartInfo
            {
                FileName = explorerPath,
                Arguments = "shell:RecycleBinFolder",
                UseShellExecute = true
            },
            SystemShortcutKind.FileExplorer => new ProcessStartInfo
            {
                FileName = explorerPath,
                UseShellExecute = true
            },
            SystemShortcutKind.PowerShell => new ProcessStartInfo
            {
                FileName = SystemShortcutSetting.GetDefaultTargetPath(
                    SystemShortcutKind.PowerShell),
                Arguments =
                    "-NoExit -Command \"Set-Location -LiteralPath " +
                    $"'{GetPowerShellWorkingDirectory()}'\"",
                WorkingDirectory = GetPowerShellWorkingDirectory(),
                UseShellExecute = true
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown Windows system shortcut.")
        };
        Process.Start(startInfo);
        return Task.CompletedTask;
    }

    private static string GetPowerShellWorkingDirectory()
    {
        const string preferredDirectory = @"D:\";
        if (Directory.Exists(preferredDirectory))
        {
            return preferredDirectory;
        }

        return Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
    }

    public async Task EmptyRecycleBinAsync(
        nint ownerWindow,
        CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(
            () => SHEmptyRecycleBinW(
                ownerWindow,
                null,
                0),
            cancellationToken);
        if (result < 0 && result != CancelledHResult)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    // Reads aggregate Recycle Bin size and item count without enumerating files.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(
        string? rootPath,
        ref RecycleBinInfo queryInfo);

    // Opens Windows' normal confirmation UI and empties only the Recycle Bin.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(
        nint ownerWindow,
        string? rootPath,
        uint flags);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinInfo
    {
        internal int Size;
        internal long TotalSize;
        internal long ItemCount;
    }
}
