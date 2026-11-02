using AnotherSurface.Core;

namespace AnotherSurface.Tests;

public sealed class WidgetPlacementTests
{
    [Fact]
    public void Defaults_KeepExistingDesktopRegionsAndLockWidgets()
    {
        var settings = new WidgetSettings();

        Assert.Equal(ClockStyle.DigitalClean, settings.ClockStyle);
        Assert.Equal(WidgetAnchor.TopRight, settings.ClockPlacement.Anchor);
        Assert.Equal(WidgetAnchor.CenterRight, settings.SystemInfoPlacement.Anchor);
        Assert.All(Enum.GetValues<WidgetKind>(), kind =>
            Assert.True(settings.GetPlacement(kind).IsLocked));
    }

    [Fact]
    public void Copy_MigratesLegacySides()
    {
        var settings = new WidgetSettings
        {
            ClockSide = WidgetSide.Left,
            SystemInfoSide = WidgetSide.Left,
        }.Copy();

        Assert.Equal(WidgetAnchor.TopLeft, settings.ClockPlacement.Anchor);
        Assert.Equal(WidgetAnchor.CenterLeft, settings.SystemInfoPlacement.Anchor);
    }

    [Fact]
    public void Copy_ClonesNewPlacementsAndPreservesExplicitLegacyOverrides()
    {
        var original = new WidgetSettings
        {
            ClockStyle = ClockStyle.DigitalStacked,
            ClockSide = WidgetSide.Left,
            ClockPlacement = new WidgetPlacement
            {
                Anchor = WidgetAnchor.Custom,
                X = 0.2,
                Y = 0.8,
                IsLocked = false,
                DisplayId = " DISPLAY2 "
            },
            SystemInfoPlacement = WidgetPlacement.FromAnchor(WidgetAnchor.BottomCenter),
        };

        var copy = original.Copy();

        Assert.Equal(ClockStyle.DigitalStacked, copy.ClockStyle);
        Assert.NotSame(original.ClockPlacement, copy.ClockPlacement);
        Assert.NotSame(original.SystemInfoPlacement, copy.SystemInfoPlacement);
        Assert.Equal(WidgetAnchor.Custom, copy.ClockPlacement.Anchor);
        Assert.Equal(0.2, copy.ClockPlacement.X);
        Assert.Equal(0.8, copy.ClockPlacement.Y);
        Assert.False(copy.ClockPlacement.IsLocked);
        Assert.Equal("DISPLAY2", copy.ClockPlacement.DisplayId);
        Assert.Equal(WidgetAnchor.BottomCenter, copy.SystemInfoPlacement.Anchor);
        original.ClockPlacement.X = 0.9;
        Assert.Equal(0.2, copy.ClockPlacement.X);
    }

    [Fact]
    public void Copy_NormalizesInvalidEnumsCoordinatesAndEmptyDisplay()
    {
        var settings = new WidgetSettings
        {
            ClockStyle = (ClockStyle)999,
            ClockSide = (WidgetSide)999,
            ClockPlacement = new WidgetPlacement
            {
                Anchor = (WidgetAnchor)999,
                X = double.NaN,
                Y = double.PositiveInfinity,
                DisplayId = " "
            },
            SystemInfoPlacement = new WidgetPlacement
            {
                Anchor = WidgetAnchor.Custom,
                X = -10,
                Y = 10
            },
        }.Copy();

        Assert.Equal(ClockStyle.DigitalClean, settings.ClockStyle);
        Assert.Equal(WidgetSide.Right, settings.ClockSide);
        Assert.Equal(WidgetAnchor.TopRight, settings.ClockPlacement.Anchor);
        Assert.Equal(0.5, settings.ClockPlacement.X);
        Assert.Equal(0.5, settings.ClockPlacement.Y);
        Assert.Null(settings.ClockPlacement.DisplayId);
        Assert.Equal(0, settings.SystemInfoPlacement.X);
        Assert.Equal(1, settings.SystemInfoPlacement.Y);
    }

    [Theory]
    [InlineData(WidgetAnchor.TopLeft, 24, 24)]
    [InlineData(WidgetAnchor.TopCenter, 400, 24)]
    [InlineData(WidgetAnchor.TopRight, 776, 24)]
    [InlineData(WidgetAnchor.CenterLeft, 24, 350)]
    [InlineData(WidgetAnchor.Center, 400, 350)]
    [InlineData(WidgetAnchor.CenterRight, 776, 350)]
    [InlineData(WidgetAnchor.BottomLeft, 24, 676)]
    [InlineData(WidgetAnchor.BottomCenter, 400, 676)]
    [InlineData(WidgetAnchor.BottomRight, 776, 676)]
    public void Resolve_AnchorsRespectWidgetSizeAndSafeMargin(
        WidgetAnchor anchor,
        double expectedX,
        double expectedY)
    {
        var position = WidgetPlacementLayout.Resolve(
            WidgetPlacement.FromAnchor(anchor), 1000, 800, 200, 100, 24);

        Assert.Equal(expectedX, position.X);
        Assert.Equal(expectedY, position.Y);
    }

    [Fact]
    public void CustomPlacement_RoundTripsDragAndScalesToChangedDisplay()
    {
        var placement = WidgetPlacementLayout.FromPosition(
            212, 513, 1000, 800, 200, 100, 24,
            isLocked: false, displayId: "DISPLAY2");
        var restored = WidgetPlacementLayout.Resolve(
            placement, 1000, 800, 200, 100, 24);
        var resized = WidgetPlacementLayout.Resolve(
            placement, 2000, 1200, 200, 100, 24);

        Assert.Equal(WidgetAnchor.Custom, placement.Anchor);
        Assert.Equal(0.25, placement.X);
        Assert.Equal(0.75, placement.Y);
        Assert.False(placement.IsLocked);
        Assert.Equal("DISPLAY2", placement.DisplayId);
        Assert.Equal(new WidgetPosition(212, 513), restored);
        Assert.Equal(new WidgetPosition(462, 813), resized);
    }

    [Fact]
    public void FromPosition_ClampsDragToDisplaySafeEdges()
    {
        var placement = WidgetPlacementLayout.FromPosition(
            -1000, 3000, 1000, 800, 200, 100, 24);
        var position = WidgetPlacementLayout.Resolve(
            placement, 1000, 800, 200, 100, 24);

        Assert.Equal(0, placement.X);
        Assert.Equal(1, placement.Y);
        Assert.Equal(new WidgetPosition(24, 676), position);
    }

    [Fact]
    public void Layout_OversizedWidgetAndInvalidDimensionsStayFinite()
    {
        var placement = WidgetPlacementLayout.FromPosition(
            double.NaN, double.PositiveInfinity,
            100, double.NaN, 300, 100, double.PositiveInfinity);
        var position = WidgetPlacementLayout.Resolve(
            placement, 100, double.NaN, 300, 100, double.PositiveInfinity);

        Assert.Equal(0.5, placement.X);
        Assert.Equal(0.5, placement.Y);
        Assert.Equal(new WidgetPosition(0, 0), position);
    }

    [Fact]
    public void SetPlacement_StoresIndependentNormalizedCopy()
    {
        var placement = new WidgetPlacement
        {
            Anchor = WidgetAnchor.Custom,
            X = -1,
            Y = 0.25,
            IsLocked = false
        };
        var settings = new WidgetSettings();
        settings.SetPlacement(WidgetKind.SystemInfo, placement);
        placement.Y = 0.9;

        Assert.Equal(0, settings.GetPlacement(WidgetKind.SystemInfo).X);
        Assert.Equal(0.25, settings.GetPlacement(WidgetKind.SystemInfo).Y);
        Assert.False(settings.GetPlacement(WidgetKind.SystemInfo).IsLocked);
    }
}
