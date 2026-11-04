using System.Globalization;
using System.IO;

namespace AnotherSurface.Windows;

/// <summary>Records clean exits without starting a watchdog process.</summary>
public static class SessionMarker
{
    public static void MarkActive(string markerPath, int ownerProcessId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, ownerProcessId.ToString(CultureInfo.InvariantCulture));
    }

    public static bool MarkCleanExit(string markerPath, int ownerProcessId)
    {
        try
        {
            if (!File.Exists(markerPath) || !int.TryParse(File.ReadAllText(markerPath).Trim(), out var owner) || owner != ownerProcessId)
                return false;
            File.Delete(markerPath);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
