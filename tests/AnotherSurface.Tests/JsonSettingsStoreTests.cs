using AnotherSurface.Core;

namespace AnotherSurface.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSettingsAndEnums()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceSettingsTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new JsonSettingsStore(path);
            var original = new AppSettings
            {
                RestoreOnStartup = false,
                LaunchAtWindowsLogin = true,
                SelectedMonitorId = "DISPLAY1",
                Dock = new DockSettings
                {
                    IsEnabled = true,
                    ShowRunningApplications = false,
                    ShowWindowsTaskbar = true,
                    IconSize = 64,
                    BottomMargin = 20
                },
                Playback = new PlaybackOptions
                {
                    Fit = WallpaperFit.Stretch,
                    IsMuted = false,
                    Volume = 0.72,
                    Brightness = 0.68,
                    Contrast = 0.82,
                    Saturation = 0.55,
                    PauseWhenForegroundAppIsMaximized = false,
                    ApplicationRules =
                    [
                        new ApplicationPlaybackRule
                        {
                            ProcessName = "explorer",
                            Condition =
                                ApplicationRuleCondition.IsForeground,
                            Action =
                                ApplicationPlaybackAction.KeepRunning
                        },
                        new ApplicationPlaybackRule
                        {
                            ProcessName = "Unity",
                            Condition =
                                ApplicationRuleCondition.IsForeground,
                            Action = ApplicationPlaybackAction.Pause
                        },
                        new ApplicationPlaybackRule
                        {
                            ProcessName = "UnrealEditor",
                            Condition =
                                ApplicationRuleCondition.IsRunning,
                            Action =
                                ApplicationPlaybackAction.StopFreeMemory
                        }
                    ]
                },
                DisplayWallpapers =
                [
                    new DisplayWallpaperProfile
                    {
                        DisplayId = "DISPLAY1",
                        Wallpaper = new WallpaperItem
                        {
                            Id = Guid.Parse(
                                "11111111-1111-1111-1111-111111111111"),
                            Name = "Primary",
                            SourcePath = @"C:\Wallpapers\primary.mp4",
                            Kind = WallpaperKind.Video
                        },
                        Playback = new PlaybackOptions
                        {
                            Fit = WallpaperFit.Fill,
                            IsMuted = true,
                            Volume = 0.15
                        }
                    },
                    new DisplayWallpaperProfile
                    {
                        DisplayId = "DISPLAY2",
                        Wallpaper = new WallpaperItem
                        {
                            Id = Guid.Parse(
                                "22222222-2222-2222-2222-222222222222"),
                            Name = "Secondary",
                            SourcePath = @"C:\Wallpapers\secondary.png",
                            Kind = WallpaperKind.Image
                        },
                        Playback = new PlaybackOptions
                        {
                            Fit = WallpaperFit.Fit,
                            IsMuted = false,
                            Volume = 0.55
                        }
                    }
                ],
                Widgets = new WidgetSettings
                {
                    IsEnabled = true,
                    ShowClock = false,
                    ShowSystemInfo = true,
                    ShowQuickSlots = true,
                    ClockSide = WidgetSide.Left,
                    SystemInfoSide = WidgetSide.Right,
                    SnapQuickSlotsToGrid = true,
                    QuickSlotGridSize = 40,
                    SystemShortcuts =
                    [
                        new SystemShortcutSetting
                        {
                            Kind = SystemShortcutKind.RecycleBin,
                            Name = "Recycle Bin",
                            IsVisible = true,
                            Slot = 4,
                            CustomIconPath = @"C:\Icons\trash.png",
                            ShowInDock = true,
                            IsRunning = true,
                            HasAttention = true
                        }
                    ],
                    QuickSlotGroups =
                    [
                        new QuickSlotGroup
                        {
                            Id = "development",
                            Name = "Development",
                            X = 640,
                            Y = 220,
                            Columns = 4
                        }
                    ],
                    QuickSlots =
                    [
                        new QuickSlotItem
                        {
                            Slot = 3,
                            Name = "Notes",
                            TargetPath = @"C:\Tools\notes.exe",
                            GroupId = "development",
                            ShowInDock = true,
                            IsRunning = true,
                            HasAttention = true
                        }
                    ]
                },
                Library =
                [
                    new WallpaperItem
                    {
                        Name = "Test",
                        SourcePath = @"C:\Wallpapers\test.mp4",
                        Kind = WallpaperKind.Video
                    }
                ]
            };

            await store.SaveAsync(original);
            var restored = await store.LoadAsync();

            Assert.False(restored.RestoreOnStartup);
            Assert.True(restored.LaunchAtWindowsLogin);
            Assert.Equal("DISPLAY1", restored.SelectedMonitorId);
            Assert.Equal(WallpaperFit.Stretch, restored.Playback.Fit);
            Assert.False(restored.Playback.IsMuted);
            Assert.Equal(0.72, restored.Playback.Volume, precision: 2);
            Assert.Equal(
                0.68,
                restored.Playback.Brightness,
                precision: 2);
            Assert.Equal(
                0.82,
                restored.Playback.Contrast,
                precision: 2);
            Assert.Equal(
                0.55,
                restored.Playback.Saturation,
                precision: 2);
            Assert.False(
                restored.Playback.PauseWhenForegroundAppIsMaximized);
            Assert.Collection(
                restored.Playback.ApplicationRules,
                rule =>
                {
                    Assert.Equal("explorer", rule.ProcessName);
                    Assert.Equal(
                        ApplicationRuleCondition.IsForeground,
                        rule.Condition);
                    Assert.Equal(
                        ApplicationPlaybackAction.KeepRunning,
                        rule.Action);
                },
                rule =>
                {
                    Assert.Equal("Unity", rule.ProcessName);
                    Assert.Equal(
                        ApplicationRuleCondition.IsForeground,
                        rule.Condition);
                    Assert.Equal(
                        ApplicationPlaybackAction.Pause,
                        rule.Action);
                },
                rule =>
                {
                    Assert.Equal("UnrealEditor", rule.ProcessName);
                    Assert.Equal(
                        ApplicationRuleCondition.IsRunning,
                        rule.Condition);
                    Assert.Equal(
                        ApplicationPlaybackAction.StopFreeMemory,
                        rule.Action);
                });
            Assert.True(restored.Dock.IsEnabled);
            Assert.False(restored.Dock.ShowRunningApplications);
            Assert.True(restored.Dock.ShowWindowsTaskbar);
            Assert.Equal(64, restored.Dock.IconSize);
            Assert.Equal(20, restored.Dock.BottomMargin);
            Assert.Collection(
                restored.DisplayWallpapers.OrderBy(
                    profile => profile.DisplayId),
                profile =>
                {
                    Assert.Equal("DISPLAY1", profile.DisplayId);
                    Assert.Equal("Primary", profile.Wallpaper?.Name);
                    Assert.Equal(WallpaperFit.Fill, profile.Playback.Fit);
                    Assert.Equal(0.15, profile.Playback.Volume, 2);
                },
                profile =>
                {
                    Assert.Equal("DISPLAY2", profile.DisplayId);
                    Assert.Equal("Secondary", profile.Wallpaper?.Name);
                    Assert.Equal(WallpaperFit.Fit, profile.Playback.Fit);
                    Assert.Equal(0.55, profile.Playback.Volume, 2);
                });
            Assert.Single(restored.Library);
            Assert.Equal(WallpaperKind.Video, restored.Library[0].Kind);
            Assert.False(restored.Widgets.ShowClock);
            Assert.True(restored.Widgets.ShowSystemInfo);
            var group = Assert.Single(
                restored.Widgets.QuickSlotGroups);
            Assert.Equal("Development", group.Name);
            Assert.Equal(640, group.X);
            Assert.Equal(240, group.Y);
            Assert.Equal(4, group.Columns);
            Assert.Equal(WidgetSide.Left, restored.Widgets.ClockSide);
            Assert.Equal(
                WidgetSide.Right,
                restored.Widgets.SystemInfoSide);
            Assert.True(restored.Widgets.SnapQuickSlotsToGrid);
            Assert.Equal(40, restored.Widgets.QuickSlotGridSize);
            var slot = Assert.Single(restored.Widgets.QuickSlots);
            Assert.Equal(3, slot.Slot);
            Assert.Equal("Notes", slot.Name);
            Assert.Equal("development", slot.GroupId);
            Assert.Equal("Development", slot.GroupName);
            Assert.True(slot.ShowInDock);
            Assert.False(slot.IsRunning);
            Assert.False(slot.HasAttention);
            var recycleBin = restored.Widgets.SystemShortcuts.Single(item =>
                item.Kind == SystemShortcutKind.RecycleBin);
            Assert.True(recycleBin.IsVisible);
            Assert.Equal(4, recycleBin.Slot);
            Assert.Equal(
                @"C:\Icons\trash.png",
                recycleBin.CustomIconPath);
            Assert.True(recycleBin.ShowInDock);
            Assert.False(recycleBin.IsRunning);
            Assert.False(recycleBin.HasAttention);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_MigratesLegacyActiveWallpaperToDisplayProfile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceDisplayMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 15,
                  "SelectedMonitorId": "DISPLAY2",
                  "ActiveWallpaper": {
                    "Id": "33333333-3333-3333-3333-333333333333",
                    "Name": "Legacy Secondary",
                    "SourcePath": "C:\\Wallpapers\\legacy.mp4",
                    "Kind": "Video",
                    "IsBuiltIn": false
                  },
                  "Playback": {
                    "IsMuted": false,
                    "Volume": 0.42,
                    "Fit": "Fit"
                  }
                }
                """);

            var settings = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            var profile = Assert.Single(settings.DisplayWallpapers);
            Assert.Equal("DISPLAY2", profile.DisplayId);
            Assert.Equal("Legacy Secondary", profile.Wallpaper?.Name);
            Assert.Equal(WallpaperFit.Fit, profile.Playback.Fit);
            Assert.False(profile.Playback.IsMuted);
            Assert.Equal(0.42, profile.Playback.Volume, 2);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_ReturnsDefaultsWhenFileDoesNotExist()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurface-Missing-{Guid.NewGuid():N}",
            "settings.json");
        var store = new JsonSettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.True(settings.RestoreOnStartup);
        Assert.True(settings.Playback.IsMuted);
        Assert.True(settings.Widgets.IsEnabled);
        Assert.True(settings.Widgets.ShowClock);
        Assert.True(settings.Widgets.ShowSystemInfo);
        Assert.Empty(settings.Library);
    }

    [Fact]
    public async Task Load_VersionTwelveUsesGeneralApplicationPolicy()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfacePlaybackMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 12,
                  "Playback": {
                    "PauseWhenForegroundAppIsMaximized": false
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.False(
                settings.Playback
                    .PauseWhenForegroundAppIsMaximized);
            Assert.Empty(settings.Playback.ApplicationRules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_PreservesEmptyVersionThirteenRuleList()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceEmptyPlaybackRulesTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 13,
                  "Playback": {
                    "ApplicationRules": []
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Empty(settings.Playback.ApplicationRules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_RemovesVersionThirteenUnrealActions()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceUnrealPauseMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 13,
                  "Playback": {
                    "ApplicationRules": [
                      {
                        "ProcessName": "UnrealEditor.exe",
                        "Condition": "IsRunning",
                        "Action": "StopFreeMemory"
                      },
                      {
                        "ProcessName": "CustomRenderer",
                        "Condition": "IsRunning",
                        "Action": "StopFreeMemory"
                      },
                      {
                        "ProcessName": "UE4Editor",
                        "Condition": "IsRunning",
                        "Action": "Pause"
                      },
                      {
                        "ProcessName": "UE5Editor",
                        "Condition": "IsForeground",
                        "Action": "StopFreeMemory"
                      }
                    ]
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            var custom = Assert.Single(
                settings.Playback.ApplicationRules);
            Assert.Equal("CustomRenderer", custom.ProcessName);
            Assert.Equal(
                ApplicationPlaybackAction.StopFreeMemory,
                custom.Action);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_RemovesVersionFourteenUnrealTriplet()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceUnrealIsolationMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 14,
                  "Playback": {
                    "ApplicationRules": [
                      { "ProcessName": "UnrealEditor.exe", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE4Editor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE5Editor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "CustomRenderer", "Condition": "IsRunning", "Action": "StopFreeMemory" },
                      { "ProcessName": "UE5Editor", "Condition": "IsForeground", "Action": "Pause" }
                    ]
                  },
                  "DisplayWallpapers": [
                    {
                      "DisplayId": "DISPLAY1",
                      "Playback": {
                        "ApplicationRules": [
                          { "ProcessName": "UnrealEditor", "Condition": "IsRunning", "Action": "StopFreeMemory" },
                          { "ProcessName": "DisplayHelper", "Condition": "IsForeground", "Action": "Pause" }
                        ]
                      }
                    }
                  ]
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            var custom = Assert.Single(
                settings.Playback.ApplicationRules);
            Assert.Equal("CustomRenderer", custom.ProcessName);
            Assert.Equal(
                ApplicationPlaybackAction.StopFreeMemory,
                custom.Action);
            var displayRule = Assert.Single(
                Assert.Single(settings.DisplayWallpapers)
                    .Playback.ApplicationRules);
            Assert.Equal("DisplayHelper", displayRule.ProcessName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    public async Task Load_RemovesPartialUnrealPauseRules(
        int schemaVersion)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceUnrealPausePreservationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                $$"""
                {
                  "SchemaVersion": {{schemaVersion}},
                  "Playback": {
                    "ApplicationRules": [
                      { "ProcessName": "UnrealEditor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "CustomRenderer", "Condition": "IsRunning", "Action": "StopFreeMemory" }
                    ]
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            var custom = Assert.Single(
                settings.Playback.ApplicationRules);
            Assert.Equal("CustomRenderer", custom.ProcessName);
            Assert.Equal(
                ApplicationPlaybackAction.StopFreeMemory,
                custom.Action);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_RemovesVersionFifteenFullUnrealPauseTriplet()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceCurrentUnrealPauseTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 15,
                  "Playback": {
                    "ApplicationRules": [
                      { "ProcessName": "UnrealEditor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE4Editor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE5Editor", "Condition": "IsRunning", "Action": "Pause" }
                    ]
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Empty(settings.Playback.ApplicationRules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_RemovesMixedVersionFourteenUnrealRules()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceMixedUnrealPauseTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 14,
                  "Playback": {
                    "ApplicationRules": [
                      { "ProcessName": "UnrealEditor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE4Editor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE5Editor", "Condition": "IsForeground", "Action": "Pause" }
                    ]
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Empty(settings.Playback.ApplicationRules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_MigratesVersionFourteenTripletWithNullRule()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceNullUnrealRuleTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 14,
                  "Playback": {
                    "ApplicationRules": [
                      null,
                      { "ProcessName": "UnrealEditor.exe", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE4Editor", "Condition": "IsRunning", "Action": "Pause" },
                      { "ProcessName": "UE5Editor", "Condition": "IsRunning", "Action": "Pause" }
                    ]
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Empty(settings.Playback.ApplicationRules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_MigratesVersionOneSettingsWithoutWidgets()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "SelectedMonitorId": "DISPLAY1",
                  "Playback": {
                    "IsMuted": false,
                    "Volume": 0.4,
                    "Fit": "Fit"
                  }
                }
                """);

            var settings = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("DISPLAY1", settings.SelectedMonitorId);
            Assert.False(settings.Playback.IsMuted);
            Assert.Equal(0.4, settings.Playback.Volume, precision: 2);
            Assert.True(settings.Widgets.IsEnabled);
            Assert.True(settings.Widgets.ShowClock);
            Assert.Empty(settings.Widgets.QuickSlots);
            Assert.Equal(4, settings.Widgets.SystemShortcuts.Count);
            Assert.Equal(3, settings.Widgets.QuickSlotGroups.Count);
            Assert.DoesNotContain(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "기본");
            Assert.Contains(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "작업");
            Assert.Contains(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "앱");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_MigratesLegacySystemQuickSlot()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceSystemMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 5,
                  "Widgets": {
                    "QuickSlotGroups": [
                      {
                        "Id": "general",
                        "Name": "General",
                        "Columns": 5
                      }
                    ],
                    "QuickSlots": [
                      {
                        "Slot": 4,
                        "Name": "Recycle Bin",
                        "TargetPath": "shell:RecycleBinFolder",
                        "GroupId": "general"
                      }
                    ]
                  }
                }
                """);

            var settings = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Empty(settings.Widgets.QuickSlots);
            var recycleBin = settings.Widgets.SystemShortcuts.Single(item =>
                item.Kind == SystemShortcutKind.RecycleBin);
            Assert.False(recycleBin.IsVisible);
            Assert.True(recycleBin.ShowInDock);
            Assert.Equal(4, recycleBin.Slot);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_AddsCompactPresetGroupsWithoutReplacingGameAlias()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceCategoryMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 6,
                  "Widgets": {
                    "SystemShortcuts": [
                      {
                        "Kind": "ThisPc",
                        "Name": "This PC",
                        "IsVisible": true,
                        "Slot": 4
                      },
                      {
                        "Kind": "FileExplorer",
                        "Name": "File Explorer",
                        "IsVisible": true,
                        "Slot": 5
                      },
                      {
                        "Kind": "RecycleBin",
                        "Name": "Recycle Bin",
                        "IsVisible": true,
                        "Slot": 6
                      }
                    ],
                    "QuickSlotGroups": [
                      {
                        "Id": "general-custom",
                        "Name": "General",
                        "X": 640,
                        "Y": 0,
                        "Columns": 5
                      },
                      {
                        "Id": "game-custom",
                        "Name": "Game",
                        "X": 80,
                        "Y": 80,
                        "Columns": 5
                      }
                    ]
                  }
                }
                """);

            var settings = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(3, settings.Widgets.QuickSlotGroups.Count);
            Assert.DoesNotContain(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "Games");
            Assert.DoesNotContain(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "기본");
            var game = settings.Widgets.QuickSlotGroups.Single(group =>
                group.Name == "게임");
            Assert.Equal("game-custom", game.Id);
            Assert.Equal(80, game.Y);
            Assert.Contains(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "작업");
            Assert.Contains(
                settings.Widgets.QuickSlotGroups,
                group => group.Name == "앱");
            Assert.Equal(4, settings.Widgets.SystemShortcuts.Count);
            var powerShell = settings.Widgets.SystemShortcuts.Single(item =>
                item.Kind == SystemShortcutKind.PowerShell);
            Assert.False(powerShell.IsVisible);
            Assert.True(powerShell.ShowInDock);
            Assert.Equal(0, powerShell.Slot);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_ConsolidatesVersionSevenCategoriesAndSlots()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceCompactCategoryTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 7,
                  "Widgets": {
                    "QuickSlotGroups": [
                      { "Id": "general", "Name": "General", "X": 1520, "Y": 880, "Columns": 5 },
                      { "Id": "game", "Name": "Game", "X": 0, "Y": 0, "Columns": 5 },
                      { "Id": "development", "Name": "Development", "X": 0, "Y": 160, "Columns": 5 },
                      { "Id": "productivity", "Name": "Productivity", "X": 0, "Y": 640, "Columns": 5 },
                      { "Id": "communication", "Name": "Communication", "X": 0, "Y": 320, "Columns": 5 },
                      { "Id": "browsers", "Name": "Browsers", "X": 0, "Y": 480, "Columns": 5 },
                      { "Id": "launchers", "Name": "Launchers", "X": 0, "Y": 800, "Columns": 5 }
                    ],
                    "QuickSlots": [
                      { "Slot": 1, "Name": "Visual Studio Code", "TargetPath": "Code.exe", "GroupId": "development", "GroupName": "Development" },
                      { "Slot": 2, "Name": "Notion", "TargetPath": "Notion.exe", "GroupId": "productivity", "GroupName": "Productivity" },
                      { "Slot": 3, "Name": "Discord", "TargetPath": "Discord.exe", "GroupId": "communication", "GroupName": "Communication" },
                      { "Slot": 4, "Name": "Firefox", "TargetPath": "firefox.exe", "GroupId": "browsers", "GroupName": "Browsers" },
                      { "Slot": 5, "Name": "Epic Games Launcher", "TargetPath": "EpicGamesLauncher.exe", "GroupId": "launchers", "GroupName": "Launchers" }
                    ]
                  }
                }
                """);

            var settings = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(
                ["게임", "앱", "작업"],
                settings.Widgets.QuickSlotGroups
                    .Select(group => group.Name)
                    .Order()
                    .ToArray());
            var work = settings.Widgets.QuickSlotGroups.Single(group =>
                group.Name == "작업");
            Assert.Equal("development", work.Id);
            Assert.Equal(160, work.Y);
            var apps = settings.Widgets.QuickSlotGroups.Single(group =>
                group.Name == "앱");
            Assert.Equal("communication", apps.Id);
            Assert.Equal(320, apps.Y);
            Assert.All(
                settings.Widgets.QuickSlots.Where(item =>
                    item.Name is "Visual Studio Code" or "Notion"),
                item => Assert.Equal("작업", item.GroupName));
            Assert.All(
                settings.Widgets.QuickSlots.Where(item =>
                    item.Name is "Discord" or "Firefox"),
                item => Assert.Equal("앱", item.GroupName));
            Assert.Equal(
                "게임",
                settings.Widgets.QuickSlots.Single(item =>
                    item.Name == "Epic Games Launcher").GroupName);
            Assert.False(settings.Widgets.ShowQuickSlots);
            Assert.All(
                settings.Widgets.QuickSlots,
                item => Assert.True(item.ShowInDock));
            Assert.Equal(
                [50, 60, 70, 80, 90],
                settings.Widgets.QuickSlots
                    .OrderBy(item => item.Slot)
                    .Select(item => item.DockOrder)
                    .ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_MigratesVersionElevenQuickSlotsIntoDock()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceDockMigrationTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 11,
                  "Widgets": {
                    "ShowQuickSlots": true,
                    "QuickSlotGroups": [
                      { "Id": "general", "Name": "기본" },
                      { "Id": "apps", "Name": "앱" }
                    ],
                    "QuickSlots": [
                      {
                        "Slot": 7,
                        "Name": "Browser",
                        "TargetPath": "browser.exe",
                        "GroupId": "general",
                        "GroupName": "기본",
                        "ShowInDock": false
                      }
                    ],
                    "SystemShortcuts": [
                      {
                        "Kind": "ThisPc",
                        "Name": "내 PC",
                        "IsVisible": true,
                        "Slot": 1,
                        "ShowInDock": false
                      }
                    ]
                  }
                }
                """);

            var settings =
                await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.False(settings.Widgets.ShowQuickSlots);
            Assert.DoesNotContain(
                settings.Widgets.QuickSlotGroups,
                group => group.Id == "general");
            var slot = Assert.Single(settings.Widgets.QuickSlots);
            Assert.True(slot.ShowInDock);
            Assert.Equal("apps", slot.GroupId);
            Assert.True(slot.DockOrder > 0);
            Assert.All(
                settings.Widgets.SystemShortcuts,
                item =>
                {
                    Assert.False(item.IsVisible);
                    Assert.True(item.ShowInDock);
                    Assert.True(item.DockOrder > 0);
                });
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
