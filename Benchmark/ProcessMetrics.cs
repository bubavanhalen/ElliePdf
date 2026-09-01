using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ElliePdf.Benchmark;

internal readonly record struct ProcessMetricSnapshot(
    long ProcessId,
    TimeSpan CpuTime,
    long PrivateBytes,
    long WorkingSetBytes);

internal sealed class ProcessTreeMetricSnapshot
{
    private ProcessTreeMetricSnapshot(IReadOnlyDictionary<long, ProcessMetricSnapshot> processes)
    {
        Processes = processes;
    }

    public IReadOnlyDictionary<long, ProcessMetricSnapshot> Processes { get; }

    public static ProcessTreeMetricSnapshot Empty { get; } = new(new Dictionary<long, ProcessMetricSnapshot>());

    public long PrivateBytes => Processes.Values.Sum(static value => value.PrivateBytes);
    public long WorkingSetBytes => Processes.Values.Sum(static value => value.WorkingSetBytes);
    public double CpuMilliseconds => Processes.Values.Sum(static value => value.CpuTime.TotalMilliseconds);

    public long RootPrivateBytes(long rootProcessId) =>
        Processes.TryGetValue(rootProcessId, out var value) ? value.PrivateBytes : 0;

    public long ChildPrivateBytes(long rootProcessId) =>
        Processes.Where(pair => pair.Key != rootProcessId).Sum(static pair => pair.Value.PrivateBytes);

    public double CpuDeltaMilliseconds(ProcessTreeMetricSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var delta = 0d;
        foreach (var current in Processes)
        {
            delta += current.Value.CpuTime.TotalMilliseconds -
                (baseline.Processes.TryGetValue(current.Key, out var old) ? old.CpuTime.TotalMilliseconds : 0);
        }

        return Math.Max(0, delta);
    }

    public static ProcessTreeMetricSnapshot Capture(long rootProcessId)
    {
        IReadOnlySet<long> processIds = OperatingSystem.IsWindows()
            ? WindowsProcessTree.Collect(rootProcessId)
            : new HashSet<long> { rootProcessId };

        var snapshots = new Dictionary<long, ProcessMetricSnapshot>();
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                snapshots[processId] = new(
                    processId,
                    process.TotalProcessorTime,
                    Math.Max(0, process.PrivateMemorySize64),
                    Math.Max(0, process.WorkingSet64));
            }
            catch (ArgumentException)
            {
                // A short-lived child can exit between process-tree enumeration and sampling.
            }
            catch (InvalidOperationException)
            {
                // The process can disappear while its counters are read. The next sample remains valid.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access to a protected process is not required for measuring the target tree.
            }
            catch (NotSupportedException)
            {
                // Some counters are unavailable on non-Windows process implementations.
            }
        }

        return new(snapshots);
    }
}

internal static class WindowsProcessTree
{
    private const uint SnapshotAllProcesses = 0x00000002;
    private const uint InvalidHandleValue = 0xFFFFFFFF;

    public static IReadOnlySet<long> Collect(long rootProcessId)
    {
        var parents = new Dictionary<long, long>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotAllProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(unchecked((int)InvalidHandleValue)))
            return new HashSet<long> { rootProcessId };

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parents[entry.ProcessId] = entry.ParentProcessId;
                    entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var result = new HashSet<long> { rootProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parents)
            {
                if (result.Contains(pair.Value) && result.Add(pair.Key))
                    changed = true;
            }
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
