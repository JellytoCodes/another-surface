using Vellume.Windows;

namespace Vellume.Tests;

public sealed class SystemMetricsSamplerTests
{
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
