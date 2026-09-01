using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Pdf.Client;

internal sealed partial class WorkerJob : IDisposable
{
    // A worker renders untrusted input. It has no legitimate reason to create a child process,
    // interact with the desktop, or survive its client.
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private readonly SafeJobHandle _handle;

    public WorkerJob(long memoryLimitBytes, uint cpuHardCapPercent)
    {
        var rawHandle = CreateJobObjectW(0, null);
        _handle = new SafeJobHandle(rawHandle);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create the PDF worker job object.");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitActiveProcess | JobObjectLimitJobMemory | JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException,
                ActiveProcessLimit = 1
            },
            JobMemoryLimit = checked((nuint)memoryLimitBytes)
        };
        SetJobInformation(_handle, JobObjectInformationClass.ExtendedLimitInformation, limits);

        var uiRestrictions = new JobObjectBasicUiRestrictions
        {
            UIRestrictionsClass = JobObjectUiLimitHandles |
                                  JobObjectUiLimitReadClipboard |
                                  JobObjectUiLimitWriteClipboard |
                                  JobObjectUiLimitSystemParameters |
                                  JobObjectUiLimitDisplaySettings |
                                  JobObjectUiLimitGlobalAtoms |
                                  JobObjectUiLimitDesktop |
                                  JobObjectUiLimitExitWindows
        };
        SetJobInformation(_handle, JobObjectInformationClass.BasicUiRestrictions, uiRestrictions);

        var cpu = new JobObjectCpuRateControlInformation
        {
            ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
            CpuRate = checked(cpuHardCapPercent * 100)
        };
        SetJobInformation(_handle, JobObjectInformationClass.CpuRateControlInformation, cpu);
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to contain the PDF worker process.");
        }
    }

    public void Dispose() => _handle.Dispose();

    private static unsafe void SetJobInformation<T>(SafeJobHandle job, JobObjectInformationClass informationClass, T value)
        where T : unmanaged
    {
        if (!SetInformationJobObject(job, informationClass, &value, (uint)sizeof(T)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to configure the PDF worker job object.");
        }
    }

    private enum JobObjectInformationClass
    {
        BasicUiRestrictions = 4,
        ExtendedLimitInformation = 9,
        CpuRateControlInformation = 15
    }

    private const uint JobObjectUiLimitHandles = 0x00000001;
    private const uint JobObjectUiLimitReadClipboard = 0x00000002;
    private const uint JobObjectUiLimitWriteClipboard = 0x00000004;
    private const uint JobObjectUiLimitSystemParameters = 0x00000008;
    private const uint JobObjectUiLimitDisplaySettings = 0x00000010;
    private const uint JobObjectUiLimitGlobalAtoms = 0x00000020;
    private const uint JobObjectUiLimitDesktop = 0x00000040;
    private const uint JobObjectUiLimitExitWindows = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicUiRestrictions
    {
        public uint UIRestrictionsClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle(nint value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInformationClass informationClass,
        void* information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

internal static partial class WorkerHandleBroker
{
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint HandleFlagInherit = 0x00000001;

    public static SafeFileHandle OpenReadOnly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    public static nint DuplicateInto(SafeFileHandle source, Process targetProcess)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetProcess);
        if (source.IsInvalid || source.IsClosed)
        {
            throw new ObjectDisposedException(nameof(source));
        }

        if (!DuplicateHandle(
                GetCurrentProcess(),
                source.DangerousGetHandle(),
                targetProcess.SafeHandle,
                out var targetHandle,
                0,
                inheritHandle: false,
                DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to broker the PDF source handle.");
        }

        return targetHandle;
    }

    public static void MakeInheritable(SafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ObjectDisposedException(nameof(handle));
        }

        if (!SetHandleInformation(handle.DangerousGetHandle(), HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to broker an inherited PDF worker handle.");
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        nint sourceProcess,
        nint sourceHandle,
        SafeProcessHandle targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
}

/// <summary>
/// Starts the native parser with a filtered version of the interactive user's token. The worker
/// retains its user SID (so the per-user named pipe works), but loses enabled privileges. It is
/// deliberately separate from the broker: all document access still arrives through duplicated
/// handles rather than ambient path access.
/// </summary>
internal static partial class WorkerRestrictedProcess
{
    private const uint DisableMaxPrivilege = 0x1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint StartfUseStdHandles = 0x00000100;

    public static WorkerProcessLaunch Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool requireAppContainer,
        bool lessPrivilegedAppContainer,
        bool requireRestrictedToken,
        IReadOnlyList<nint>? additionalInheritedHandles = null)
    {
        try
        {
            return WorkerAppContainerProcess.Start(
                executablePath,
                arguments,
                workingDirectory,
                lessPrivilegedAppContainer,
                additionalInheritedHandles);
        }
        catch (Exception exception) when (!requireAppContainer && IsSandboxSetupFailure(exception))
        {
            try
            {
                return StartRestricted(executablePath, arguments, workingDirectory);
            }
            catch (Win32Exception restrictedException) when (!requireRestrictedToken && restrictedException.NativeErrorCode is 5 or 1314)
            {
                // Some developer shells and enterprise endpoint products prohibit assigning a
                // filtered primary token. This final compatibility state remains observable and
                // is never selected by a Release-default client.
                return StartJobConstrained(executablePath, arguments, workingDirectory);
            }
        }
    }

    private static bool IsSandboxSetupFailure(Exception exception)
        => exception is Win32Exception
            or System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException
            or PlatformNotSupportedException;

    private static WorkerProcessLaunch StartRestricted(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var currentToken = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
        if (!CreateRestrictedToken(currentToken.AccessToken, DisableMaxPrivilege, 0, nint.Zero, 0, nint.Zero, 0, nint.Zero, out var restrictedToken))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create a restricted PDF worker token.");
        }

        using (restrictedToken)
        {
            var standardInput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var startup = new StartupInfo
            {
                cb = (uint)Marshal.SizeOf<StartupInfo>(),
                dwFlags = StartfUseStdHandles,
                hStdInput = standardInput.ClientSafePipeHandle.DangerousGetHandle(),
                hStdOutput = GetStdHandle(-11),
                hStdError = GetStdHandle(-12)
            };
            var commandLine = new StringBuilder(Quote(executablePath));
            foreach (var argument in arguments)
            {
                commandLine.Append(' ').Append(Quote(argument));
            }

            if (!CreateProcessAsUserW(
                    restrictedToken,
                    executablePath,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    inheritHandles: true,
                    CreateNoWindow | CreateSuspended,
                    nint.Zero,
                    workingDirectory,
                    in startup,
                    out var processInformation))
            {
                standardInput.Dispose();
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"Unable to start the PDF worker with a restricted token (Win32 error {error}).");
            }

            standardInput.DisposeLocalCopyOfClientHandle();
            try
            {
                // The Process instance takes its own query handle; the inherited pipe endpoint is
                // closed in this process before the secret is written.
                return new WorkerProcessLaunch(
                    Process.GetProcessById(unchecked((int)processInformation.dwProcessId)),
                    standardInput,
                    WorkerSandboxMode.RestrictedToken,
                    new SafeKernelHandle(processInformation.hThread, ownsHandle: true));
            }
            catch
            {
                standardInput.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(processInformation.hProcess);
            }
        }
    }

    private static WorkerProcessLaunch StartJobConstrained(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new PdfWorkerUnavailableException("The PDF worker process could not be started.");
        return new WorkerProcessLaunch(process, process.StandardInput.BaseStream, WorkerSandboxMode.JobConstrainedCompatibility);
    }

    private static string Quote(string value) => '"' + value.Replace("\\\"", "\\\\\"") + '"';

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CreateRestrictedToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(
        SafeAccessTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        nint sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token,
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        in StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

internal sealed partial class WorkerProcessLaunch : IDisposable
{
    private SafeKernelHandle? _suspendedThread;

    public WorkerProcessLaunch(
        Process process,
        Stream standardInput,
        WorkerSandboxMode sandboxMode,
        SafeKernelHandle? suspendedThread = null)
    {
        Process = process;
        StandardInput = standardInput;
        SandboxMode = sandboxMode;
        _suspendedThread = suspendedThread;
    }

    public Process Process { get; }
    public Stream StandardInput { get; }
    public WorkerSandboxMode SandboxMode { get; }

    public void ResumeAfterContainment()
    {
        var thread = Interlocked.Exchange(ref _suspendedThread, null);
        if (thread is null)
        {
            return;
        }

        using (thread)
        {
            if (ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to resume the contained PDF worker.");
            }
        }
    }

    public void Dispose()
    {
        _suspendedThread?.Dispose();
        _suspendedThread = null;
        StandardInput.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeKernelHandle thread);
}

internal sealed partial class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle(nint value, bool ownsHandle) : base(ownsHandle) => SetHandle(value);
    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
