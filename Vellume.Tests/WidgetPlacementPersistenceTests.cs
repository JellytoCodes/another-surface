using Vellume.Core;

namespace Vellume.Tests;

public sealed class WidgetPlacementPersistenceTests
{
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
                    CodexPlacement = WidgetPlacement.FromAnchor(
                        WidgetAnchor.CenterRight, isLocked: false)
                }
            });

            var restored = await store.LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, restored.SchemaVersion);
            Assert.Equal(style, restored.Widgets.ClockStyle);
            Assert.Equal(WidgetAnchor.Custom, restored.Widgets.ClockPlacement.Anchor);
            Assert.Equal(0.31, restored.Widgets.ClockPlacement.X);
            Assert.Equal(0.72, restored.Widgets.ClockPlacement.Y);
            Assert.False(restored.Widgets.ClockPlacement.IsLocked);
            Assert.Equal("DISPLAY2", restored.Widgets.ClockPlacement.DisplayId);
            Assert.Equal(WidgetAnchor.BottomCenter, restored.Widgets.SystemInfoPlacement.Anchor);
            Assert.True(restored.Widgets.SystemInfoPlacement.IsLocked);
            Assert.Equal(WidgetAnchor.CenterRight, restored.Widgets.CodexPlacement.Anchor);
            Assert.False(restored.Widgets.CodexPlacement.IsLocked);
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
            Assert.Equal(WidgetAnchor.TopLeft, restored.Widgets.CodexPlacement.Anchor);
            Assert.Equal(WidgetSide.Left, restored.Widgets.CodexCompanionSide);
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
            Assert.Equal(WidgetAnchor.Custom, restored.Widgets.CodexPlacement.Anchor);
            Assert.Equal(0.9, restored.Widgets.CodexPlacement.X);
            Assert.Equal(0.2, restored.Widgets.CodexPlacement.Y);
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
            Assert.Equal(WidgetAnchor.TopLeft, restored.Widgets.CodexPlacement.Anchor);
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
