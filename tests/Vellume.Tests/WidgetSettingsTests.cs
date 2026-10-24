using Vellume.Core;

namespace Vellume.Tests;

public sealed class WidgetSettingsTests
{
    [Fact]
    public void Copy_ClonesSortsAndFiltersQuickSlots()
    {
        var group = new QuickSlotGroup
        {
            Id = "development",
            Name = "Development",
            X = 720,
            Y = 180,
            Columns = 4
        };
        var original = new WidgetSettings
        {
            ClockSide = WidgetSide.Left,
            SystemInfoSide = WidgetSide.Right,
            SnapQuickSlotsToGrid = true,
            QuickSlotGridSize = 40,
            QuickSlotGroups = [group],
            SystemShortcuts =
            [
                new SystemShortcutSetting
                {
                    Kind = SystemShortcutKind.ThisPc,
                    Name = "This PC",
                    IsVisible = true,
                    ShowInDock = false,
                    Slot = 2
                },
                new SystemShortcutSetting
                {
                    Kind = SystemShortcutKind.RecycleBin,
                    Name = "Recycle Bin",
                    IsVisible = true,
                    ShowInDock = true,
                    Slot = 9,
                    CustomIconPath = @"C:\Icons\bin.png"
                }
            ],
            QuickSlots =
            [
                new QuickSlotItem
                {
                    Slot = 8,
                    Name = "Eight",
                    TargetPath = "8.exe",
                    GroupId = "missing",
                    ShowInDock = true
                },
                new QuickSlotItem { Slot = 0, Name = "Invalid", TargetPath = "0.exe" },
                new QuickSlotItem
                {
                    Slot = 2,
                    Name = "Two",
                    TargetPath = "2.exe",
                    GroupId = "development"
                },
                new QuickSlotItem
                {
                    Slot = 12,
                    Name = "Desktop click only",
                    TargetPath = "12.exe",
                    GroupId = "development"
                },
                new QuickSlotItem { Slot = 100, Name = "Invalid", TargetPath = "100.exe" }
            ]
        };

        var copy = original.Copy();

        Assert.Equal([2, 8, 12], copy.QuickSlots.Select(item => item.Slot));
        Assert.NotSame(original.QuickSlots[0], copy.QuickSlots[1]);
        Assert.All(
            copy.QuickSlots,
            item => Assert.Equal("development", item.GroupId));
        Assert.All(
            copy.QuickSlots,
            item => Assert.Equal("Development", item.GroupName));
        var copiedGroup = Assert.Single(copy.QuickSlotGroups);
        Assert.NotSame(group, copiedGroup);
        Assert.Equal(720, copiedGroup.X);
        Assert.Equal(200, copiedGroup.Y);
        Assert.Equal(4, copiedGroup.Columns);
        Assert.Equal(WidgetSide.Left, copy.ClockSide);
        Assert.Equal(WidgetSide.Right, copy.SystemInfoSide);
        Assert.True(copy.SnapQuickSlotsToGrid);
        Assert.Equal(40, copy.QuickSlotGridSize);
        Assert.Equal(4, copy.SystemShortcuts.Count);
        Assert.Contains(
            copy.SystemShortcuts,
            item => item.Kind == SystemShortcutKind.PowerShell);
        var thisPc = copy.SystemShortcuts.Single(item =>
            item.Kind == SystemShortcutKind.ThisPc);
        Assert.True(thisPc.IsVisible);
        Assert.Equal(1, thisPc.Slot);
        Assert.False(thisPc.ShowInDock);
        var recycleBin = copy.SystemShortcuts.Single(item =>
            item.Kind == SystemShortcutKind.RecycleBin);
        Assert.True(recycleBin.IsVisible);
        Assert.Equal(9, recycleBin.Slot);
        Assert.Equal(@"C:\Icons\bin.png", recycleBin.CustomIconPath);
        Assert.True(recycleBin.ShowInDock);
        Assert.True(copy.QuickSlots.Single(item =>
            item.Slot == 8).ShowInDock);

        var rendered = copy.GetDesktopQuickSlots(
            recycleBinHasItems: true);
        Assert.Equal(5, rendered.Count);
        var renderedRecycleBin = rendered.Single(item =>
            item.SystemKind == SystemShortcutKind.RecycleBin);
        Assert.True(renderedRecycleBin.IsDynamicStateActive);
        Assert.Equal("development", renderedRecycleBin.GroupId);
    }
}
