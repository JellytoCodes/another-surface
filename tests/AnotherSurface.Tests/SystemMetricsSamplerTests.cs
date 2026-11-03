using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class SystemMetricsSamplerTests
{
    [Fact]
    public void GpuUsage_SumsProcessesOnTheSamePhysicalEngine()
    {
        var actual = SystemMetricsSampler.AggregateGpuUsage(
        [
            ("pid_10_luid_0x0_0x1_phys_0_eng_2_engtype_VideoDecode", 19.7),
            ("pid_20_luid_0x0_0x1_phys_0_eng_2_engtype_VideoDecode", 13.8),
            ("pid_30_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 12.0)
        ]);
        Assert.Equal(33.5, actual!.Value, 6);
    }

    [Fact]
    public void GpuUsage_DoesNotSumDifferentAdaptersOrEngineIndices()
    {
        var actual = SystemMetricsSampler.AggregateGpuUsage(
        [
            ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 30),
            ("pid_20_luid_0x0_0x1_phys_0_eng_1_engtype_3D", 40),
            ("pid_30_luid_0x0_0x2_phys_0_eng_0_engtype_3D", 50),
            ("pid_40_luid_0x0_0x1_phys_1_eng_0_engtype_3D", 60)
        ]);
        Assert.Equal(60, actual);
    }

    [Fact]
    public void GpuUsage_RejectsUnavailableSamplesAndClampsOnlyTheFinalTotal()
    {
        Assert.Null(SystemMetricsSampler.AggregateGpuUsage(
        [
            ("not-an-engine", 20),
            ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", double.NaN),
            ("pid_20_luid_0x0_0x1_phys_0_eng_0_engtype_3D", -1),
            ("pid_30_luid_0x0_0x1_phys_0_eng_0_engtype_3D", double.PositiveInfinity)
        ]));
        Assert.Equal(0, SystemMetricsSampler.AggregateGpuUsage(
            [("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 0)]));
        Assert.Equal(100, SystemMetricsSampler.AggregateGpuUsage(
        [
            ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 65),
            ("pid_20_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 60)
        ]));
    }

    [Fact]
    public void RapidSamplesReuseSlowGpuAndDriveMeasurements()
    {
        using var sampler = new SystemMetricsSampler();

        _ = sampler.Sample();
        _ = sampler.Sample();

        Assert.Equal(1, sampler.GpuSampleCount);
        Assert.Equal(1, sampler.DriveSampleCount);
    }
}
