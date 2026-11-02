using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnotherSurface.Core;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _accessLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _accessLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false) ?? new AppSettings();

            // Keep older local settings usable as new optional sections are added.
            var sourceSchemaVersion = settings.SchemaVersion;
            settings.Library ??= [];
            settings.Playback ??= new PlaybackOptions();
            settings.DisplayWallpapers ??= [];
            settings.Widgets ??= new WidgetSettings();
            settings.Dock ??= new DockSettings();
            settings.Widgets.QuickSlots ??= [];
            settings.Widgets.QuickSlotGroups ??= [];
            settings.Widgets.SystemShortcuts ??= [];
            if (sourceSchemaVersion < 5)
            {
                foreach (var group in settings.Widgets.QuickSlotGroups)
                {
                    group.Columns = 5;
                }
            }
            if (sourceSchemaVersion < 6)
            {
                MigrateLegacySystemShortcuts(settings.Widgets);
            }
            if (sourceSchemaVersion < 7)
            {
                AddPowerShellShortcut(settings.Widgets);
            }
            if (sourceSchemaVersion < 8)
            {
                ConsolidatePresetGroups(settings.Widgets);
            }
            if (sourceSchemaVersion < 10)
            {
                LocalizePresetNames(settings.Widgets);
            }
            if (sourceSchemaVersion < 12)
            {
                MigrateQuickSlotsToDock(settings.Widgets);
            }
            if (sourceSchemaVersion < 13 &&
                (settings.Playback.ApplicationRules is null ||
                 settings.Playback.ApplicationRules.Count == 0))
            {
                settings.Playback.ApplicationRules =
                    ApplicationPlaybackRule.CreateDefaults();
            }
            settings.Playback.ApplicationRules ??=
                ApplicationPlaybackRule.CreateDefaults();
            if (sourceSchemaVersion < 16 &&
                settings.ActiveWallpaper is not null &&
                !string.IsNullOrWhiteSpace(settings.SelectedMonitorId) &&
                settings.FindDisplayProfile(settings.SelectedMonitorId) is null)
            {
                settings.DisplayWallpapers.Add(
                    new DisplayWallpaperProfile
                    {
                        DisplayId = settings.SelectedMonitorId,
                        Wallpaper = settings.ActiveWallpaper,
                        Playback = settings.Playback.Copy()
                    });
            }

            if (sourceSchemaVersion < 17)
            {
                RemoveUnrealSpecificRules(settings.Playback);
                foreach (var profile in settings.DisplayWallpapers)
                {
                    if (profile?.Playback is not null)
                    {
                        RemoveUnrealSpecificRules(profile.Playback);
                    }
                }
            }

            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            settings.Playback = settings.Playback.Copy();
            settings.DisplayWallpapers = settings.DisplayWallpapers
                .Where(profile =>
                    profile is not null &&
                    !string.IsNullOrWhiteSpace(profile.DisplayId))
                .GroupBy(
                    profile => profile.DisplayId.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last().Copy())
                .ToList();
            settings.Dock = settings.Dock.Copy();
            settings.Widgets = settings.Widgets.Copy();
            return settings;
        }
        finally
        {
            _accessLock.Release();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _accessLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            settings.Playback =
                (settings.Playback ?? new PlaybackOptions()).Copy();
            settings.DisplayWallpapers =
                (settings.DisplayWallpapers ?? [])
                .Where(profile =>
                    profile is not null &&
                    !string.IsNullOrWhiteSpace(profile.DisplayId))
                .GroupBy(
                    profile => profile.DisplayId.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last().Copy())
                .ToList();
            settings.Dock = (settings.Dock ?? new DockSettings()).Copy();
            settings.Widgets = (settings.Widgets ?? new WidgetSettings()).Copy();
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("The settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = _settingsPath + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            _accessLock.Release();
        }
    }

    private static void RemoveUnrealSpecificRules(
        PlaybackOptions playback)
    {
        playback.ApplicationRules =
            (playback.ApplicationRules ?? [])
            .Where(rule =>
                rule is not null &&
                !IsUnrealEditorProcessName(rule.ProcessName))
            .ToList();
    }

    private static bool IsUnrealEditorProcessName(
        string? processName)
    {
        var normalized = NormalizeExecutableName(processName);
        return normalized.Equals(
                   "UnrealEditor",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(
                   "UE4Editor",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(
                   "UE5Editor",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExecutableName(
        string? processName)
    {
        var normalized = processName?.Trim() ?? string.Empty;
        if (normalized.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    private static void MigrateLegacySystemShortcuts(
        WidgetSettings widgets)
    {
        var defaults = SystemShortcutSetting.CreateDefaults();
        var windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var explorerPath = Path.Combine(
            windowsDirectory,
            "explorer.exe");

        foreach (var slot in widgets.QuickSlots.ToArray())
        {
            SystemShortcutKind? kind =
                slot.TargetPath.Equals(
                    "shell:MyComputerFolder",
                    StringComparison.OrdinalIgnoreCase)
                    ? SystemShortcutKind.ThisPc
                    : slot.TargetPath.Equals(
                        "shell:RecycleBinFolder",
                        StringComparison.OrdinalIgnoreCase)
                        ? SystemShortcutKind.RecycleBin
                        : slot.TargetPath.Equals(
                            explorerPath,
                            StringComparison.OrdinalIgnoreCase)
                            ? SystemShortcutKind.FileExplorer
                            : null;
            if (kind is null)
            {
                continue;
            }

            var systemItem = defaults.First(item =>
                item.Kind == kind.Value);
            if (!systemItem.IsVisible)
            {
                systemItem.IsVisible = true;
                systemItem.Slot = slot.Slot;
            }

            widgets.QuickSlots.Remove(slot);
        }

        widgets.SystemShortcuts = defaults;
    }

    private static void ConsolidatePresetGroups(WidgetSettings widgets)
    {
        widgets.QuickSlotGroups ??= [];
        widgets.QuickSlots ??= [];

        var workSourceIds = GetGroupIds(
            widgets,
            "Work",
            "작업",
            "Development",
            "Productivity");
        var appsSourceIds = GetGroupIds(
            widgets,
            "Apps",
            "앱",
            "Communication",
            "Browsers");
        var gameSourceIds = GetGroupIds(
            widgets,
            "Game",
            "게임",
            "Games",
            "Launchers");

        var general = FindOrCreateGroup(
            widgets,
            "general",
            "기본",
            "General");
        var work = FindOrCreateGroup(
            widgets,
            "work",
            "작업",
            "Work",
            "Development",
            "Productivity");
        var apps = FindOrCreateGroup(
            widgets,
            "apps",
            "앱",
            "Apps",
            "Communication",
            "Browsers");
        var game = FindOrCreateGroup(
            widgets,
            "game",
            "게임",
            "Game",
            "Games",
            "Launchers");
        general.Name = "기본";
        work.Name = "작업";
        apps.Name = "앱";
        game.Name = "게임";
        workSourceIds.Add(work.Id);
        appsSourceIds.Add(apps.Id);
        gameSourceIds.Add(game.Id);

        foreach (var slot in widgets.QuickSlots)
        {
            QuickSlotGroup? target = null;
            if (IsEpicGamesLauncher(slot))
            {
                target = game;
            }
            else if (workSourceIds.Contains(slot.GroupId) ||
                     HasName(
                         slot.GroupName,
                         "작업",
                         "Work",
                         "Development",
                         "Productivity"))
            {
                target = work;
            }
            else if (appsSourceIds.Contains(slot.GroupId) ||
                     HasName(
                         slot.GroupName,
                         "앱",
                         "Apps",
                         "Communication",
                         "Browsers"))
            {
                target = apps;
            }
            else if (gameSourceIds.Contains(slot.GroupId) ||
                     HasName(
                         slot.GroupName,
                         "게임",
                         "Game",
                         "Games",
                         "Launchers"))
            {
                target = game;
            }

            if (target is not null)
            {
                slot.GroupId = target.Id;
                slot.GroupName = target.Name;
            }
        }

        widgets.QuickSlotGroups.RemoveAll(group =>
            (!string.Equals(
                group.Id,
                work.Id,
                StringComparison.OrdinalIgnoreCase) &&
             HasName(
                 group.Name,
                 "작업",
                 "Work",
                 "Development",
                 "Productivity")) ||
            (!string.Equals(
                group.Id,
                apps.Id,
                StringComparison.OrdinalIgnoreCase) &&
             HasName(
                 group.Name,
                 "앱",
                 "Apps",
                 "Communication",
                 "Browsers")) ||
            (!string.Equals(
                group.Id,
                game.Id,
                StringComparison.OrdinalIgnoreCase) &&
             HasName(
                 group.Name,
                 "게임",
                 "Game",
                 "Games",
                 "Launchers")));
    }

    private static HashSet<string> GetGroupIds(
        WidgetSettings widgets,
        params string[] names)
        => widgets.QuickSlotGroups
            .Where(group => HasName(group.Name, names))
            .Select(group => group.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static QuickSlotGroup FindOrCreateGroup(
        WidgetSettings widgets,
        string presetId,
        params string[] preferredNames)
    {
        var byId = widgets.QuickSlotGroups.FirstOrDefault(group =>
            string.Equals(
                group.Id,
                presetId,
                StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        foreach (var name in preferredNames)
        {
            var existing = widgets.QuickSlotGroups.FirstOrDefault(group =>
                string.Equals(
                    group.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }

        var preset = presetId.Equals(
            "general",
            StringComparison.OrdinalIgnoreCase)
            ? new QuickSlotGroup
            {
                Id = "general",
                Name = "기본",
                X = 80,
                Y = 80,
                Columns = 5
            }
            : QuickSlotGroup.CreatePresetGroups()
                .Single(group => group.Id.Equals(
                    presetId,
                    StringComparison.OrdinalIgnoreCase));
        widgets.QuickSlotGroups.Add(preset);
        return preset;
    }

    private static void LocalizePresetNames(WidgetSettings widgets)
    {
        var localizedNames = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["general"] = "기본",
            ["work"] = "작업",
            ["apps"] = "앱",
            ["game"] = "게임"
        };

        foreach (var group in widgets.QuickSlotGroups)
        {
            if (localizedNames.TryGetValue(group.Id, out var localized))
            {
                group.Name = localized;
                continue;
            }

            group.Name = group.Name switch
            {
                "General" => "기본",
                "Work" => "작업",
                "Apps" => "앱",
                "Game" or "Games" => "게임",
                _ => group.Name
            };
        }

        var groupNames = widgets.QuickSlotGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.Id))
            .ToDictionary(
                group => group.Id,
                group => group.Name,
                StringComparer.OrdinalIgnoreCase);
        foreach (var slot in widgets.QuickSlots)
        {
            if (groupNames.TryGetValue(slot.GroupId, out var groupName))
            {
                slot.GroupName = groupName;
            }
        }

        foreach (var shortcut in widgets.SystemShortcuts)
        {
            if (string.IsNullOrWhiteSpace(shortcut.Name) ||
                shortcut.Name is "This PC" or "File Explorer" or
                    "Recycle Bin")
            {
                shortcut.Name =
                    SystemShortcutSetting.GetDefaultName(shortcut.Kind);
            }
        }
    }

    private static bool IsEpicGamesLauncher(QuickSlotItem slot)
        => string.Equals(
               slot.Name,
               "Epic Games Launcher",
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               Path.GetFileName(slot.TargetPath),
               "EpicGamesLauncher.exe",
               StringComparison.OrdinalIgnoreCase);

    private static bool HasName(
        string? value,
        params string[] candidates)
        => candidates.Any(candidate =>
            string.Equals(
                value,
                candidate,
                StringComparison.OrdinalIgnoreCase));

    private static void AddPowerShellShortcut(WidgetSettings widgets)
    {
        widgets.SystemShortcuts ??= [];
        if (widgets.SystemShortcuts.Any(item =>
                item.Kind == SystemShortcutKind.PowerShell))
        {
            return;
        }

        var powerShell = SystemShortcutSetting.CreateDefaults()
            .Single(item =>
                item.Kind == SystemShortcutKind.PowerShell);
        powerShell.IsVisible = true;
        widgets.SystemShortcuts.Add(powerShell);
    }

    private static void MigrateQuickSlotsToDock(WidgetSettings widgets)
    {
        widgets.ShowQuickSlots = false;
        widgets.QuickSlots ??= [];
        widgets.SystemShortcuts ??= [];
        widgets.QuickSlotGroups ??= [];

        var generalIds = widgets.QuickSlotGroups
            .Where(group =>
                group.Id.Equals(
                    "general",
                    StringComparison.OrdinalIgnoreCase) ||
                group.Name.Equals(
                    "기본",
                    StringComparison.OrdinalIgnoreCase) ||
                group.Name.Equals(
                    "General",
                    StringComparison.OrdinalIgnoreCase))
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        widgets.QuickSlotGroups.RemoveAll(group =>
            generalIds.Contains(group.Id));
        if (widgets.QuickSlotGroups.Count == 0)
        {
            widgets.QuickSlotGroups =
                QuickSlotGroup.CreatePresetGroups();
        }

        var fallbackGroup =
            widgets.QuickSlotGroups.FirstOrDefault(group =>
                group.Id.Equals(
                    "apps",
                    StringComparison.OrdinalIgnoreCase)) ??
            widgets.QuickSlotGroups[0];
        foreach (var slot in widgets.QuickSlots)
        {
            slot.ShowInDock = true;
            if (generalIds.Contains(slot.GroupId) ||
                string.IsNullOrWhiteSpace(slot.GroupId))
            {
                slot.GroupId = fallbackGroup.Id;
                slot.GroupName = fallbackGroup.Name;
            }
        }

        var order = 10;
        foreach (var system in widgets.SystemShortcuts
                     .OrderBy(item => item.Kind))
        {
            system.IsVisible = false;
            system.ShowInDock = true;
            system.DockOrder = order;
            order += 10;
        }
        foreach (var slot in widgets.QuickSlots
                     .OrderBy(item => item.DockOrder <= 0
                         ? int.MaxValue
                         : item.DockOrder)
                     .ThenBy(item => item.Slot))
        {
            slot.DockOrder = order;
            order += 10;
        }
    }
}
