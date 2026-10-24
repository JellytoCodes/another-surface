using Vellume.Core;

namespace Vellume.Tests;

public sealed class WidgetPlacementPersistenceTests
{
    [Fact]
    public async Task LoadAndSave_IgnoresRemovedWidgetFieldsAndKeepsExistingSettings()
    {
        await WithStoreAsync(async (store, path) =>
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "SchemaVersion": 19,
                  "Widgets": {
                    "ShowCodexCompanion": true,
                    "CodexCompanionSide": "Left",
                    "CodexScale": 1.3,
                    "CodexPlacement": { "Anchor": "Custom", "X": 0.9, "Y": 0.2 },
                    "ClockColor": "#89E0D5",
                    "ClockScale": 1.137,
                    "ClockPlacement": { "Anchor": "Custom", "X": 0.31, "Y": 0.72, "IsLocked": false },
                    "SystemInfoAppearance": { "Scale": 1.173 }
                  },
                  "Dock": { "IsEnabled": false, "IconSize": 54 }
                }
                """);
            var settings = await store.LoadAsync();
            Assert.Equal("#89E0D5", settings.Widgets.ClockColor);
            Assert.Equal(1.137, settings.Widgets.ClockScale);
            Assert.Equal(0.31, settings.Widgets.ClockPlacement.X);
            Assert.Equal(0.72, settings.Widgets.ClockPlacement.Y);
            Assert.False(settings.Widgets.ClockPlacement.IsLocked);
            Assert.Equal(1.173, settings.Widgets.SystemInfoAppearance.Scale);
            Assert.False(settings.Dock.IsEnabled);
            Assert.Equal(54, settings.Dock.IconSize);
            Assert.Equal(new[] { WidgetKind.Clock, WidgetKind.SystemInfo }, Enum.GetValues<WidgetKind>());
            await store.SaveAsync(settings);
            Assert.DoesNotContain("Codex", await File.ReadAllTextAsync(path));
        });
    }

    [Theory]
    [InlineData(ClockStyle.AnalogMinimal)]
    [InlineData(ClockStyle.AnalogNumerals)]
    [InlineData(ClockStyle.DigitalClean)]
    [InlineData(ClockStyle.DigitalStacked)]
    [InlineData(ClockStyle.DigitalMono)]
    public async Task SaveAndLoad_RoundTripsStylePositionsLockAndDisplay(
        ClockStyle style)
    {
        await WithStoreAsync(async (store, path) =>
        {
            await store.SaveAsync(new AppSettings
            {
                Widgets = new WidgetSettings
                {
                    ClockStyle = style,
                    ClockColor = "#89E0D5",
                    ClockScale = 1.137,
                    SystemInfoAppearance = new SystemInfoAppearance { Scale = 1.173 },
                    ClockPlacement = new WidgetPlacement
                    {
                        Anchor = WidgetAnchor.Custom,
                        X = 0.31,
                        Y = 0.72,
                        IsLocked = false,
                        DisplayId = "DISPLAY2"
                    },
                    SystemInfoPlacement = WidgetPlacement.FromAnchor(
                        WidgetAnchor.BottomCenter),
                }
            });

            var restored = await store.LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, restored.SchemaVersion);
            Assert.Equal(style, restored.Widgets.ClockStyle);
            Assert.Equal("#89E0D5", restored.Widgets.ClockColor);
            Assert.Equal(1.137, restored.Widgets.ClockScale);
            Assert.Equal(1.173, restored.Widgets.SystemInfoAppearance.Scale);
            Assert.Equal(WidgetAnchor.Custom, restored.Widgets.ClockPlacement.Anchor);
            Assert.Equal(0.31, restored.Widgets.ClockPlacement.X);
            Assert.Equal(0.72, restored.Widgets.ClockPlacement.Y);
            Assert.False(restored.Widgets.ClockPlacement.IsLocked);
            Assert.Equal("DISPLAY2", restored.Widgets.ClockPlacement.DisplayId);
            Assert.Equal(WidgetAnchor.BottomCenter, restored.Widgets.SystemInfoPlacement.Anchor);
            Assert.True(restored.Widgets.SystemInfoPlacement.IsLocked);
            Assert.Contains($"\"ClockStyle\": \"{style}\"", await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task Load_MigratesOldSidesWhenPlacementWasAbsent()
    {
        await WithStoreAsync(async (store, path) =>
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "SchemaVersion": 18,
                  "Widgets": {
                    "ClockSide": "Left",
                    "SystemInfoSide": "Left",
                    "CodexCompanionSide": "Right"
                  }
                }
                """);

            var restored = await store.LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, restored.SchemaVersion);
            Assert.Equal(ClockStyle.DigitalClean, restored.Widgets.ClockStyle);
            Assert.Equal(WidgetAnchor.TopLeft, restored.Widgets.ClockPlacement.Anchor);
            Assert.Equal(WidgetAnchor.CenterLeft, restored.Widgets.SystemInfoPlacement.Anchor);
            Assert.True(restored.Widgets.ClockPlacement.IsLocked);
            await store.SaveAsync(restored);
            var reloaded = await store.LoadAsync();
            Assert.Equal(WidgetAnchor.TopLeft, reloaded.Widgets.ClockPlacement.Anchor);
        });
    }

    [Fact]
    public async Task Load_ExplicitPlacementTakesPrecedenceOverLegacySide()
    {
        await WithStoreAsync(async (store, path) =>
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "SchemaVersion": 18,
                  "Widgets": {
                    "ClockSide": "Left",
                    "ClockPlacement": { "Anchor": "Center", "IsLocked": false },
                    "SystemInfoSide": "Left",
                    "SystemInfoPlacement": { "Anchor": "BottomRight" },
                    "CodexPlacement": { "Anchor": "Custom", "X": 0.9, "Y": 0.2 }
                  }
                }
                """);

            var restored = await store.LoadAsync();

            Assert.Equal(WidgetAnchor.Center, restored.Widgets.ClockPlacement.Anchor);
            Assert.False(restored.Widgets.ClockPlacement.IsLocked);
            Assert.Equal(WidgetAnchor.BottomRight, restored.Widgets.SystemInfoPlacement.Anchor);
        });
    }

    [Fact]
    public async Task Load_NormalizesInvalidValuesAndNullPlacements()
    {
        await WithStoreAsync(async (store, path) =>
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "SchemaVersion": 19,
                  "Widgets": {
                    "ClockStyle": 999,
                    "ClockSide": "Left",
                    "ClockPlacement": null,
                    "SystemInfoPlacement": {
                      "Anchor": 999,
                      "X": -50,
                      "Y": 50,
                      "DisplayId": " DISPLAY2 "
                    },
                    "CodexPlacement": null
                  }
                }
                """);

            var restored = await store.LoadAsync();

            Assert.Equal(ClockStyle.DigitalClean, restored.Widgets.ClockStyle);
            Assert.Equal(WidgetAnchor.TopLeft, restored.Widgets.ClockPlacement.Anchor);
            Assert.Equal(WidgetAnchor.CenterRight, restored.Widgets.SystemInfoPlacement.Anchor);
            Assert.Equal(0, restored.Widgets.SystemInfoPlacement.X);
            Assert.Equal(1, restored.Widgets.SystemInfoPlacement.Y);
            Assert.Equal("DISPLAY2", restored.Widgets.SystemInfoPlacement.DisplayId);
        });
    }

    [Fact]
    public async Task Save_NormalizesNonfiniteCoordinatesBeforeSerialization()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.SaveAsync(new AppSettings
            {
                Widgets = new WidgetSettings
                {
                    ClockPlacement = new WidgetPlacement
                    {
                        Anchor = WidgetAnchor.Custom,
                        X = double.NaN,
                        Y = double.NegativeInfinity
                    }
                }
            });

            var restored = await store.LoadAsync();

            Assert.Equal(0.5, restored.Widgets.ClockPlacement.X);
            Assert.Equal(0.5, restored.Widgets.ClockPlacement.Y);
        });
    }

    [Theory]
    [InlineData("#123abc", "#123ABC")]
    [InlineData(" 89e0d5 ", "#89E0D5")]
    [InlineData(null, "#FFFFFF")]
    [InlineData("", "#FFFFFF")]
    [InlineData("#FF223344", "#FFFFFF")]
    [InlineData("##123456", "#FFFFFF")]
    [InlineData("#zzzzzz", "#FFFFFF")]
    public void Copy_NormalizesClockColor_AndDefaultsInvalidData(string? input, string expected)
    {
        var settings = new WidgetSettings { ClockColor = input! };
        Assert.Equal(expected, settings.Copy().ClockColor);
        Assert.Equal("#FFFFFF", new WidgetSettings().ClockColor);
    }

    private static async Task WithStoreAsync(
        Func<JsonSettingsStore, string, Task> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"VellumeWidgetPlacementTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await action(new JsonSettingsStore(path), path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
