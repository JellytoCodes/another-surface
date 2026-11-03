using System.IO;
using System.Runtime.InteropServices;

namespace AnotherSurface.Windows;

internal sealed class SystemMetricsSampler : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const long GpuSampleIntervalMilliseconds = 1_000;
    private const long DriveSampleIntervalMilliseconds = 30_000;
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasCpuBaseline;
    private readonly object _gpuSync = new();
    private nint _gpuQuery;
    private nint _gpuCounter;
    private nint _gpuBuffer;
    private uint _gpuBufferSize;
    private bool _gpuInitializationAttempted;
    private long _lastGpuSampleTick;
    private double? _lastGpuUsage;
    private long _lastDriveSampleTick;
    private DriveMetricsSnapshot _lastDriveC =
        DriveMetricsSnapshot.Unavailable("C");
    private DriveMetricsSnapshot _lastDriveD =
        DriveMetricsSnapshot.Unavailable("D");

    internal int GpuSampleCount { get; private set; }
    internal int DriveSampleCount { get; private set; }

    public SystemMetricsSnapshot Sample()
    {
        var cpuUsage = SampleCpuUsage();
        var now = Environment.TickCount64;
        if (_lastGpuSampleTick == 0 ||
            now - _lastGpuSampleTick >= GpuSampleIntervalMilliseconds)
        {
            GpuSampleCount++;
            _lastGpuUsage = SampleGpuUsage();
            _lastGpuSampleTick = now;
        }
        if (_lastDriveSampleTick == 0 ||
            now - _lastDriveSampleTick >= DriveSampleIntervalMilliseconds)
        {
            DriveSampleCount++;
            SampleDrives();
            _lastDriveSampleTick = Environment.TickCount64;
        }

        var memory = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            memory.TotalPhysical = 1;
        }

        var memoryUsed = memory.TotalPhysical - memory.AvailablePhysical;
        var memoryPercent = memory.TotalPhysical == 0
            ? 0
            : memoryUsed * 100d / memory.TotalPhysical;

        return new SystemMetricsSnapshot(
            Math.Clamp(cpuUsage, 0, 100),
            _lastGpuUsage is null
                ? null
                : Math.Clamp(_lastGpuUsage.Value, 0, 100),
            Math.Clamp(memoryPercent, 0, 100),
            memoryUsed,
            memory.TotalPhysical,
            _lastDriveC,
            _lastDriveD,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public void Dispose()
    {
        lock (_gpuSync)
        {
            if (_gpuQuery != nint.Zero)
            {
                PdhCloseQuery(_gpuQuery);
                _gpuQuery = nint.Zero;
                _gpuCounter = nint.Zero;
            }
            if (_gpuBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(_gpuBuffer);
                _gpuBuffer = nint.Zero;
                _gpuBufferSize = 0;
            }
        }
    }

    private double SampleCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var currentIdle = ToUInt64(idle);
        var currentKernel = ToUInt64(kernel);
        var currentUser = ToUInt64(user);
        if (!_hasCpuBaseline)
        {
            _previousIdle = currentIdle;
            _previousKernel = currentKernel;
            _previousUser = currentUser;
            _hasCpuBaseline = true;
            return 0;
        }

        var idleDelta = currentIdle - _previousIdle;
        var kernelDelta = currentKernel - _previousKernel;
        var userDelta = currentUser - _previousUser;
        _previousIdle = currentIdle;
        _previousKernel = currentKernel;
        _previousUser = currentUser;

        var total = kernelDelta + userDelta;
        return total == 0
            ? 0
            : (total - idleDelta) * 100d / total;
    }

    private double? SampleGpuUsage()
    {
        lock (_gpuSync)
        {
            return SampleGpuUsageCore();
        }
    }

    private double? SampleGpuUsageCore()
    {
        try
        {
            if (!_gpuInitializationAttempted)
            {
                _gpuInitializationAttempted = true;
                if (PdhOpenQueryW(null, nint.Zero, out _gpuQuery) != 0 ||
                    PdhAddEnglishCounterW(
                        _gpuQuery,
                        @"\GPU Engine(*)\Utilization Percentage",
                        nint.Zero,
                        out _gpuCounter) != 0)
                {
                    Dispose();
                    return null;
                }

                PdhCollectQueryData(_gpuQuery);
                return null;
            }

            if (_gpuQuery == nint.Zero ||
                _gpuCounter == nint.Zero ||
                PdhCollectQueryData(_gpuQuery) != 0)
            {
                return null;
            }

            uint bufferSize = 0;
            uint itemCount = 0;
            var status = PdhGetFormattedCounterArrayW(
                _gpuCounter,
                PdhFormatDouble,
                ref bufferSize,
                ref itemCount,
                nint.Zero);
            if (status != PdhMoreData ||
                bufferSize == 0 ||
                itemCount == 0)
            {
                return null;
            }

            EnsureGpuBuffer(bufferSize);
            var availableSize = _gpuBufferSize;
            status = PdhGetFormattedCounterArrayW(
                _gpuCounter,
                PdhFormatDouble,
                ref availableSize,
                ref itemCount,
                _gpuBuffer);
            if (status == PdhMoreData &&
                availableSize > _gpuBufferSize)
            {
                EnsureGpuBuffer(availableSize);
                availableSize = _gpuBufferSize;
                status = PdhGetFormattedCounterArrayW(
                    _gpuCounter,
                    PdhFormatDouble,
                    ref availableSize,
                    ref itemCount,
                    _gpuBuffer);
            }
            if (status != 0)
            {
                return null;
            }

            var itemSize =
                Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var samples = new List<(string Instance, double Usage)>((int)itemCount);
            for (var index = 0; index < itemCount; index++)
            {
                var item =
                    Marshal.PtrToStructure<PdhFormattedCounterValueItem>(
                        _gpuBuffer + index * itemSize);
                if (item.Value.Status is not 0 and not 1 ||
                    double.IsNaN(item.Value.DoubleValue) ||
                    double.IsInfinity(item.Value.DoubleValue))
                {
                    continue;
                }

                var instance = Marshal.PtrToStringUni(item.Name);
                if (instance is not null)
                    samples.Add((instance, item.Value.DoubleValue));
            }

            return AggregateGpuUsage(samples);
        }
        catch (DllNotFoundException)
        {
            Dispose();
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            Dispose();
            return null;
        }
    }

    internal static double? AggregateGpuUsage(IEnumerable<(string Instance, double Usage)> samples)
    {
        // PDH exposes one instance per process and physical GPU engine. Task
        // Manager totals processes on each engine, then reports the busiest
        // engine. Taking the largest process undercounts concurrent workloads;
        // summing different engines would overcount parallel GPU work.
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, usage) in samples)
        {
            if (!double.IsFinite(usage) || usage < 0) continue;
            var start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            var end = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end <= start) continue;
            var engine = instance[start..end];
            engines[engine] = engines.GetValueOrDefault(engine) + usage;
        }
        return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    private void EnsureGpuBuffer(uint requiredSize)
    {
        if (_gpuBuffer != nint.Zero &&
            _gpuBufferSize >= requiredSize)
        {
            return;
        }

        if (_gpuBuffer != nint.Zero)
        {
            Marshal.FreeHGlobal(_gpuBuffer);
        }
        _gpuBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        _gpuBufferSize = requiredSize;
    }

    private void SampleDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives();
            _lastDriveC = SampleDrive(drives, "C");
            _lastDriveD = SampleDrive(drives, "D");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            _lastDriveC = DriveMetricsSnapshot.Unavailable("C");
            _lastDriveD = DriveMetricsSnapshot.Unavailable("D");
        }
    }

    private static DriveMetricsSnapshot SampleDrive(
        IReadOnlyList<DriveInfo> drives,
        string driveLetter)
    {
        try
        {
            var drive = drives.FirstOrDefault(candidate =>
                candidate.Name.StartsWith(
                    driveLetter + ":",
                    StringComparison.OrdinalIgnoreCase));
            if (drive is null || !drive.IsReady)
            {
                return DriveMetricsSnapshot.Unavailable(driveLetter);
            }

            var total = (ulong)Math.Max(0, drive.TotalSize);
            var free = (ulong)Math.Max(0, drive.AvailableFreeSpace);
            var used = total >= free ? total - free : 0;
            var percent = total == 0
                ? 0
                : used * 100d / total;
            return new DriveMetricsSnapshot(
                driveLetter,
                true,
                Math.Clamp(percent, 0, 100),
                used,
                total);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return DriveMetricsSnapshot.Unavailable(driveLetter);
        }
    }

    private static ulong ToUInt64(FileTime value)
        => ((ulong)value.High << 32) | value.Low;

    // Reads cumulative idle, kernel, and user times without performance counters.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    // Reads physical-memory totals locally from the Windows kernel.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // Opens a local PDH query used for GPU engine utilization.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(
        string? dataSource,
        nint userData,
        out nint query);

    // Uses the locale-independent English GPU counter path.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        nint query,
        string counterPath,
        nint userData,
        out nint counter);

    // Takes the next sample for CPU-engine style PDH counters.
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    // Reads all wildcard GPU engine instances in one local buffer.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint itemBuffer);

    // Releases the native GPU query when Another Surface exits.
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        internal uint Status;
        internal double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        internal nint Name;
        internal PdhFormattedCounterValue Value;
    }
}

internal sealed record SystemMetricsSnapshot(
    double CpuPercent,
    double? GpuPercent,
    double MemoryPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    DriveMetricsSnapshot DriveC,
    DriveMetricsSnapshot DriveD,
    TimeSpan Uptime)
{
    internal double DiskPercent => DriveC.Percent;
    internal ulong DiskUsedBytes => DriveC.UsedBytes;
    internal ulong DiskTotalBytes => DriveC.TotalBytes;
}

internal sealed record DriveMetricsSnapshot(
    string Letter,
    bool IsAvailable,
    double Percent,
    ulong UsedBytes,
    ulong TotalBytes)
{
    internal static DriveMetricsSnapshot Unavailable(string letter)
        => new(letter, false, 0, 0, 0);
}
