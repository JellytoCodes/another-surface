using System.Text.Json.Serialization;

namespace Vellume.Core;

public enum WallpaperKind
{
    Image,
    Gif,
    Video,
    Web
}

public enum WallpaperFit
{
    Fill,
    Fit,
    Stretch
}

public enum WallpaperPlaybackState
{
    Stopped,
    Playing,
    Paused,
    Error
}

public enum ApplicationPlaybackAction
{
    KeepRunning,
    Pause,
    StopFreeMemory
}

public enum ApplicationRuleCondition
{
    IsForeground,
    IsRunning
}

public enum WidgetSide
{
    Left,
    Right
}

public enum SystemShortcutKind
{
    ThisPc,
    FileExplorer,
    RecycleBin,
    PowerShell
}

public sealed class WallpaperItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string OriginalSourcePath { get; set; } = string.Empty;
    public WallpaperKind Kind { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsManaged { get; set; }
    [JsonIgnore]
    public string StorageLabel => IsManaged
        ? $"{Kind} · Vellume 라이브러리"
        : $"{Kind} · 외부 경로";

    public WallpaperItem Copy() => new()
    {
        Id = Id,
        Name = Name,
        SourcePath = SourcePath,
        OriginalSourcePath = OriginalSourcePath,
        Kind = Kind,
        IsBuiltIn = IsBuiltIn,
        IsManaged = IsManaged
    };
}

public sealed class DisplayInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
    public string AssignedWallpaperName { get; set; } = "미적용";
    public bool HasAssignedWallpaper { get; set; }
    public bool IsWallpaperPaused { get; set; }
    public string WallpaperStatusLabel =>
        !HasAssignedWallpaper
            ? "중지됨"
            : IsWallpaperPaused
                ? "일시정지됨"
                : "재생 중";
    public string PauseButtonLabel =>
        IsWallpaperPaused ? "재생" : "일시정지";

    public override string ToString()
        => $"{Name} · {Width} × {Height}{(IsPrimary ? " · 기본 모니터" : string.Empty)}";
}

public sealed class DisplayWallpaperProfile
{
    public string DisplayId { get; set; } = string.Empty;
    public WallpaperItem? Wallpaper { get; set; }
    public PlaybackOptions Playback { get; set; } = new();

    public DisplayWallpaperProfile Copy() => new()
    {
        DisplayId = DisplayId?.Trim() ?? string.Empty,
        Wallpaper = Wallpaper?.Copy(),
        Playback = (Playback ?? new PlaybackOptions()).Copy()
    };
}

public sealed class ApplicationPlaybackRule
{
    public string ProcessName { get; set; } = string.Empty;
    public ApplicationRuleCondition Condition { get; set; } =
        ApplicationRuleCondition.IsForeground;
    public ApplicationPlaybackAction Action { get; set; } =
        ApplicationPlaybackAction.Pause;

    public ApplicationPlaybackRule Copy() => new()
    {
        ProcessName = ProcessName?.Trim() ?? string.Empty,
        Condition = Condition,
        Action = Action
    };

    public static List<ApplicationPlaybackRule> CreateDefaults() => [];
}

public sealed class PlaybackOptions
{
    public bool IsMuted { get; set; } = true;
    public double Volume { get; set; } = 0.25;
    public WallpaperFit Fit { get; set; } = WallpaperFit.Fill;
    public double Brightness { get; set; } = 1;
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public bool UseOptimizedVideo { get; set; } = true;
    public bool PauseWhenForegroundAppIsMaximized { get; set; } = false;
    public List<ApplicationPlaybackRule> ApplicationRules { get; set; } =
        ApplicationPlaybackRule.CreateDefaults();

    public PlaybackOptions Copy() => new()
    {
        IsMuted = IsMuted,
        Volume = Math.Clamp(Volume, 0, 1),
        Fit = Fit,
        Brightness = Math.Clamp(Brightness, 0.25, 1.25),
        Contrast = Math.Clamp(Contrast, 0.5, 1.5),
        Saturation = Math.Clamp(Saturation, 0, 1.5),
        UseOptimizedVideo = UseOptimizedVideo,
        PauseWhenForegroundAppIsMaximized =
            PauseWhenForegroundAppIsMaximized,
        ApplicationRules = (ApplicationRules ?? [])
            .OfType<ApplicationPlaybackRule>()
            .Select(rule => rule.Copy())
            .ToList()
    };
}

public sealed class QuickSlotItem
{
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public SystemShortcutKind? SystemKind { get; set; }
    public bool IsDynamicStateActive { get; set; }
    public bool ShowInDock { get; set; }
    public int DockOrder { get; set; }
    [JsonIgnore]
    public bool IsRunning { get; set; }
    [JsonIgnore]
    public bool HasAttention { get; set; }

    public QuickSlotItem Copy() => new()
    {
        Slot = Math.Clamp(Slot, 1, 99),
        Name = Name,
        TargetPath = TargetPath,
        IconPath = IconPath,
        GroupId = GroupId,
        GroupName = GroupName,
        SystemKind = SystemKind,
        IsDynamicStateActive = IsDynamicStateActive,
        ShowInDock = ShowInDock,
        DockOrder = Math.Max(0, DockOrder),
        IsRunning = IsRunning,
        HasAttention = HasAttention
    };
}

public sealed class SystemShortcutSetting
{
    public SystemShortcutKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public int Slot { get; set; }
    public string CustomIconPath { get; set; } = string.Empty;
    public bool ShowInDock { get; set; } = true;
    public int DockOrder { get; set; }
    [JsonIgnore]
    public bool IsRunning { get; set; }
    [JsonIgnore]
    public bool HasAttention { get; set; }

    public SystemShortcutSetting Copy() => new()
    {
        Kind = Kind,
        Name = string.IsNullOrWhiteSpace(Name)
            ? GetDefaultName(Kind)
            : Name.Trim(),
        IsVisible = IsVisible,
        Slot = Slot is >= 1 and <= 99 ? Slot : 0,
        CustomIconPath = CustomIconPath?.Trim() ?? string.Empty,
        ShowInDock = ShowInDock,
        DockOrder = Math.Max(0, DockOrder),
        IsRunning = IsRunning,
        HasAttention = HasAttention
    };

    public static List<SystemShortcutSetting> CreateDefaults() =>
    [
        new()
        {
            Kind = SystemShortcutKind.ThisPc,
            Name = GetDefaultName(SystemShortcutKind.ThisPc),
            DockOrder = 10
        },
        new()
        {
            Kind = SystemShortcutKind.FileExplorer,
            Name = GetDefaultName(SystemShortcutKind.FileExplorer),
            DockOrder = 20
        },
        new()
        {
            Kind = SystemShortcutKind.RecycleBin,
            Name = GetDefaultName(SystemShortcutKind.RecycleBin),
            DockOrder = 30
        },
        new()
        {
            Kind = SystemShortcutKind.PowerShell,
            Name = GetDefaultName(SystemShortcutKind.PowerShell),
            DockOrder = 40
        }
    ];

    public static string GetDefaultName(SystemShortcutKind kind)
        => kind switch
        {
            SystemShortcutKind.ThisPc => "내 PC",
            SystemShortcutKind.FileExplorer => "파일 탐색기",
            SystemShortcutKind.RecycleBin => "휴지통",
            SystemShortcutKind.PowerShell => "PowerShell",
            _ => kind.ToString()
        };

    public static string GetDefaultTargetPath(SystemShortcutKind kind)
    {
        var windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (kind == SystemShortcutKind.PowerShell)
        {
            var powerShell7 = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "PowerShell",
                "7",
                "pwsh.exe");
            if (File.Exists(powerShell7))
            {
                return powerShell7;
            }

            return Path.Combine(
                windowsDirectory,
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
        }

        return kind == SystemShortcutKind.FileExplorer
            ? Path.Combine(windowsDirectory, "explorer.exe")
            : $"system:{kind}";
    }
}

public sealed class DockSettings
{
    public bool IsEnabled { get; set; } = true;
    public bool ShowRunningApplications { get; set; } = true;
    public bool ShowWindowsTaskbar { get; set; }
    public int IconSize { get; set; } = 54;
    public int BottomMargin { get; set; } = 12;

    public DockSettings Copy() => new()
    {
        IsEnabled = IsEnabled,
        ShowRunningApplications = ShowRunningApplications,
        ShowWindowsTaskbar = ShowWindowsTaskbar,
        IconSize = Math.Clamp(IconSize, 40, 80),
        BottomMargin = Math.Clamp(BottomMargin, 0, 80)
    };
}

public sealed class QuickSlotGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "앱";
    public int X { get; set; } = 360;
    public int Y { get; set; } = 70;
    public int Columns { get; set; } = 5;

    public QuickSlotGroup Copy() => new()
    {
        Id = Id,
        Name = Name,
        X = Math.Clamp(X, 0, 10000),
        Y = Math.Clamp(Y, 0, 10000),
        Columns = Math.Clamp(Columns, 1, 10)
    };

    public static List<QuickSlotGroup> CreatePresetGroups() =>
    [
        CreatePreset("work", "작업", 80, 320),
        CreatePreset("apps", "앱", 500, 320),
        CreatePreset("game", "게임", 920, 320)
    ];

    private static QuickSlotGroup CreatePreset(
        string id,
        string name,
        int x,
        int y)
        => new()
        {
            Id = id,
            Name = name,
            X = x,
            Y = y,
            Columns = 5
        };
}

public sealed class WidgetSettings
{
    public bool IsEnabled { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowSystemInfo { get; set; } = true;
    public bool ShowQuickSlots { get; set; }
    public bool ShowCodexCompanion { get; set; } = true;
    public WidgetSide ClockSide { get; set; } = WidgetSide.Right;
    public WidgetSide SystemInfoSide { get; set; } = WidgetSide.Right;
    // Retained for backward-compatible JSON reads. The companion has a
    // fixed upper-left placement so it remains predictable.
    public WidgetSide CodexCompanionSide { get; set; } = WidgetSide.Left;
    public bool SnapQuickSlotsToGrid { get; set; } = true;
    public int QuickSlotGridSize { get; set; } = 40;
    public List<QuickSlotItem> QuickSlots { get; set; } = [];
    public List<SystemShortcutSetting> SystemShortcuts { get; set; } =
        SystemShortcutSetting.CreateDefaults();
    public List<QuickSlotGroup> QuickSlotGroups { get; set; } =
        QuickSlotGroup.CreatePresetGroups();

    public WidgetSettings Copy()
    {
        var gridSize = Math.Clamp(QuickSlotGridSize, 10, 200);
        var groups = QuickSlotGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.Id))
            .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Copy())
            .ToList();
        if (groups.Count == 0)
        {
            groups.Add(new QuickSlotGroup());
        }
        if (SnapQuickSlotsToGrid)
        {
            foreach (var group in groups)
            {
                group.X = SnapToGrid(group.X, gridSize);
                group.Y = SnapToGrid(group.Y, gridSize);
            }
        }

        var validGroupIds = groups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbackGroupId = groups[0].Id;
        var slots = QuickSlots
            .Where(item => item.Slot is >= 1 and <= 99)
            .OrderBy(item => item.Slot)
            .GroupBy(item => item.Slot)
            .Select(group => group.First())
            .Select(item =>
            {
                var copy = item.Copy();
                if (!validGroupIds.Contains(copy.GroupId))
                {
                    copy.GroupId = fallbackGroupId;
                }

                copy.GroupName = groups.First(group =>
                    group.Id.Equals(
                        copy.GroupId,
                        StringComparison.OrdinalIgnoreCase)).Name;
                return copy;
            })
            .ToList();
        var existingSystemShortcuts = (SystemShortcuts ?? [])
            .GroupBy(item => item.Kind)
            .ToDictionary(group => group.Key, group => group.First());
        var systemShortcuts = SystemShortcutSetting.CreateDefaults()
            .Select(defaultItem =>
                existingSystemShortcuts.TryGetValue(
                    defaultItem.Kind,
                    out var existing)
                    ? existing.Copy()
                    : defaultItem)
            .ToList();
        var occupiedSlots = slots
            .Select(item => item.Slot)
            .ToHashSet();
        foreach (var systemItem in systemShortcuts.Where(item =>
                     item.IsVisible))
        {
            if (systemItem.Slot is not (>= 1 and <= 99) ||
                occupiedSlots.Contains(systemItem.Slot))
            {
                systemItem.Slot = Enumerable.Range(1, 99)
                    .FirstOrDefault(slot => !occupiedSlots.Contains(slot));
            }

            if (systemItem.Slot == 0)
            {
                systemItem.IsVisible = false;
                continue;
            }

            occupiedSlots.Add(systemItem.Slot);
        }

        return new WidgetSettings
        {
            IsEnabled = IsEnabled,
            ShowClock = ShowClock,
            ShowSystemInfo = ShowSystemInfo,
            ShowQuickSlots = ShowQuickSlots,
            ShowCodexCompanion = ShowCodexCompanion,
            ClockSide = ClockSide,
            SystemInfoSide = SystemInfoSide,
            CodexCompanionSide = WidgetSide.Left,
            SnapQuickSlotsToGrid = SnapQuickSlotsToGrid,
            QuickSlotGridSize = gridSize,
            QuickSlots = slots,
            SystemShortcuts = systemShortcuts,
            QuickSlotGroups = groups
        };
    }

    public IReadOnlyList<QuickSlotItem> GetDesktopQuickSlots(
        bool recycleBinHasItems)
    {
        var result = QuickSlots
            .Select(item => item.Copy())
            .ToList();
        var generalGroup = QuickSlotGroups.FirstOrDefault(group =>
                               group.Id.Equals(
                                   "general",
                                   StringComparison.OrdinalIgnoreCase)) ??
                           QuickSlotGroups.FirstOrDefault(group =>
                               group.Name.Equals(
                                   "기본",
                                   StringComparison.OrdinalIgnoreCase) ||
                               group.Name.Equals(
                                   "General",
                                   StringComparison.OrdinalIgnoreCase)) ??
                           QuickSlotGroups.First();

        foreach (var systemItem in SystemShortcuts
                     .Where(item => item.IsVisible &&
                                    item.Slot is >= 1 and <= 99))
        {
            result.Add(new QuickSlotItem
            {
                Slot = systemItem.Slot,
                Name = systemItem.Name,
                TargetPath =
                    SystemShortcutSetting.GetDefaultTargetPath(
                        systemItem.Kind),
                IconPath = systemItem.CustomIconPath,
                GroupId = generalGroup.Id,
                GroupName = generalGroup.Name,
                SystemKind = systemItem.Kind,
                IsDynamicStateActive =
                    systemItem.Kind == SystemShortcutKind.RecycleBin &&
                    recycleBinHasItems,
                IsRunning = systemItem.IsRunning,
                HasAttention = systemItem.HasAttention
            });
        }

        return result
            .OrderBy(item => item.Slot)
            .ToList();
    }

    private static int SnapToGrid(int value, int gridSize)
        => Math.Max(
            0,
            (int)Math.Round(
                value / (double)gridSize,
                MidpointRounding.AwayFromZero) * gridSize);
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 18;
    public List<WallpaperItem> Library { get; set; } = [];
    public WallpaperItem? ActiveWallpaper { get; set; }
    public string? SelectedMonitorId { get; set; }
    public PlaybackOptions Playback { get; set; } = new();
    public List<DisplayWallpaperProfile> DisplayWallpapers { get; set; } = [];
    public WidgetSettings Widgets { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public bool RestoreOnStartup { get; set; } = true;
    public bool LaunchAtWindowsLogin { get; set; }

    public DisplayWallpaperProfile GetOrCreateDisplayProfile(
        string displayId)
    {
        DisplayWallpapers ??= [];
        var normalizedId = displayId?.Trim() ?? string.Empty;
        var profile = DisplayWallpapers.FirstOrDefault(candidate =>
            candidate.DisplayId.Equals(
                normalizedId,
                StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            return profile;
        }

        profile = new DisplayWallpaperProfile
        {
            DisplayId = normalizedId,
            Playback = (Playback ?? new PlaybackOptions()).Copy()
        };
        DisplayWallpapers.Add(profile);
        return profile;
    }

    public DisplayWallpaperProfile? FindDisplayProfile(string displayId)
        => (DisplayWallpapers ?? []).FirstOrDefault(candidate =>
            candidate.DisplayId.Equals(
                displayId?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
}

public sealed class WallpaperStateChangedEventArgs(
    WallpaperPlaybackState state,
    string message) : EventArgs
{
    public WallpaperPlaybackState State { get; } = state;
    public string Message { get; } = message;
}

public sealed class QuickSlotLayoutChangedEventArgs(
    string groupId,
    int x,
    int y) : EventArgs
{
    public string GroupId { get; } = groupId;
    public int X { get; } = x;
    public int Y { get; } = y;
}
