using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Pdf.Client;

/// <summary>
/// Creates the native parser in a capability-free AppContainer. The profile SID receives
/// read/execute access to only the app-private worker payload; documents and output files remain
/// available solely through handles duplicated by <see cref="WorkerHandleBroker"/>.
/// </summary>
internal static partial class WorkerAppContainerProcess
{
    internal const string ProfileName = "ElliePdf.PdfWorker.NoCapabilities.v1";

    private const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7);
    private const int ErrorInsufficientBuffer = 122;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly nint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private static readonly nint ProcThreadAttributeHandleList = 0x00020002;
    private static readonly nint ProcThreadAttributeAllApplicationPackagesPolicy = 0x0002000F;
    private const uint ProcessCreationAllApplicationPackagesOptOut = 0x1;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private static readonly SecurityIdentifier AllApplicationPackagesSid = new("S-1-15-2-1");
    private static readonly Lock ProfileSync = new();

    public static NamedPipeServerStream CreateBrokerPipeServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var appContainerSid = GetOrCreateProfileSid();
        var appContainerIdentity = new SecurityIdentifier(appContainerSid.DangerousGetHandle());
        var userIdentity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The PDF broker does not have a user SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userIdentity);
        security.AddAccessRule(new PipeAccessRule(
            userIdentity,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            appContainerIdentity,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security,
            HandleInheritability.None);
    }

    /// <summary>
    /// Returns the host-visible object-manager name for an object created in the worker's
    /// private AppContainer namespace.
    /// </summary>
    public static string QualifyAppContainerNamedObject(string localName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        var objectName = localName.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase)
            ? localName["Local\\".Length..]
            : localName;
        if (objectName.Length == 0 || objectName.Contains('\\'))
        {
            throw new ArgumentException("The AppContainer object name must be a single local name.", nameof(localName));
        }

        using var appContainerSid = GetOrCreateProfileSid();
        uint requiredLength = 0;
        _ = GetAppContainerNamedObjectPath(
            nint.Zero,
            appContainerSid.DangerousGetHandle(),
            0,
            null,
            ref requiredLength);
        var error = Marshal.GetLastPInvokeError();
        if (requiredLength == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, "Unable to size the PDF worker AppContainer object namespace.");
        }

        var namespacePath = new StringBuilder(checked((int)requiredLength));
        if (!GetAppContainerNamedObjectPath(
                nint.Zero,
                appContainerSid.DangerousGetHandle(),
                requiredLength,
                namespacePath,
                ref requiredLength))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to resolve the PDF worker AppContainer object namespace.");
        }

        return $"{namespacePath}\\{objectName}";
    }

    public static WorkerProcessLaunch Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool lessPrivileged,
        IReadOnlyList<nint>? additionalInheritedHandles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var appContainerSid = GetOrCreateProfileSid();
        GrantPayloadReadAndExecute(
            executablePath,
            workingDirectory,
            appContainerSid.DangerousGetHandle(),
            lessPrivileged);

        using var outputSink = CreateInheritedOutputSink();
        using var attributes = new ProcessThreadAttributeList(lessPrivileged ? 3 : 2);
        var standardInput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        try
        {

            var securityCapabilities = new SecurityCapabilities
            {
                AppContainerSid = appContainerSid.DangerousGetHandle(),
                Capabilities = nint.Zero,
                CapabilityCount = 0,
                Reserved = 0
            };
            attributes.AddStructure(ProcThreadAttributeSecurityCapabilities, securityCapabilities);
            var inheritedHandles = new nint[checked(2 + (additionalInheritedHandles?.Count ?? 0))];
            inheritedHandles[0] = standardInput.ClientSafePipeHandle.DangerousGetHandle();
            inheritedHandles[1] = outputSink.DangerousGetHandle();
            if (additionalInheritedHandles is not null)
            {
                for (var index = 0; index < additionalInheritedHandles.Count; index++)
                {
                    inheritedHandles[index + 2] = additionalInheritedHandles[index];
                }
            }
            attributes.AddHandles(ProcThreadAttributeHandleList, inheritedHandles);

            if (lessPrivileged)
            {
                attributes.AddUInt32(
                    ProcThreadAttributeAllApplicationPackagesPolicy,
                    ProcessCreationAllApplicationPackagesOptOut);
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = standardInput.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdOutput = outputSink.DangerousGetHandle(),
                    hStdError = outputSink.DangerousGetHandle()
                },
                AttributeList = attributes.DangerousGetHandle()
            };
            var commandLine = BuildCommandLine(executablePath, arguments);

            if (!CreateProcessW(
                executablePath,
                commandLine,
                nint.Zero,
                nint.Zero,
                inheritHandles: true,
                CreateNoWindow | CreateSuspended | ExtendedStartupInfoPresent,
                nint.Zero,
                workingDirectory,
                ref startup,
                out var processInformation))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(
                    error,
                    $"Unable to start the PDF worker in a capability-free {(lessPrivileged ? "LPAC" : "AppContainer")} sandbox (Win32 error {error}).");
            }

            using var processHandle = new SafeKernelHandle(processInformation.Process, ownsHandle: true);
            var threadHandle = new SafeKernelHandle(processInformation.Thread, ownsHandle: true);
            try
            {
                var process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));
                standardInput.DisposeLocalCopyOfClientHandle();
                return new WorkerProcessLaunch(
                    process,
                    standardInput,
                    lessPrivileged ? WorkerSandboxMode.LessPrivilegedAppContainer : WorkerSandboxMode.AppContainer,
                    threadHandle);
            }
            catch
            {
                _ = TerminateProcess(processHandle, 1);
                threadHandle.Dispose();
                throw;
            }
        }
        catch
        {
            standardInput.Dispose();
            throw;
        }
    }

    private static SafeSidHandle GetOrCreateProfileSid()
    {
        // userenv profile creation is not reliably race-safe when several document clients
        // start together. Serialize create/derive so one process never observes the profile
        // between those two states.
        lock (ProfileSync)
        {
            var result = CreateAppContainerProfile(
                ProfileName,
                "ElliePdf PDF worker",
                "Capability-free sandbox for parsing untrusted PDF documents.",
                nint.Zero,
                0,
                out var sid);
            if (result == 0)
            {
                return new SafeSidHandle(sid);
            }

            if (result != ErrorAlreadyExistsHResult)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            result = DeriveAppContainerSidFromAppContainerName(ProfileName, out sid);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            return new SafeSidHandle(sid);
        }
    }

    private static void GrantPayloadReadAndExecute(
        string executablePath,
        string payloadDirectory,
        nint sidPointer,
        bool lessPrivileged)
    {
        var fullPayloadDirectory = Path.GetFullPath(payloadDirectory);
        if (!Directory.Exists(fullPayloadDirectory))
        {
            throw new DirectoryNotFoundException($"The PDF worker payload directory does not exist: {fullPayloadDirectory}");
        }

        var identity = new SecurityIdentifier(sidPointer);
        var acceptedIdentities = lessPrivileged
            ? new[] { identity }
            : new[] { identity, AllApplicationPackagesSid };
        if (HasInstallerProvisionedPayloadAccess(fullPayloadDirectory, acceptedIdentities))
        {
            return;
        }

        if (IsProtectedInstallLocation(fullPayloadDirectory))
        {
            throw new UnauthorizedAccessException(
                "The installed PDF worker payload ACL does not grant inherited read/execute access " +
                $"to the {(lessPrivileged ? "worker AppContainer SID" : "worker or ALL APPLICATION PACKAGES SID")}. " +
                "The installer must provision this ACL; ElliePdf will not mutate Program Files at runtime.");
        }

        GrantDirectory(fullPayloadDirectory, identity);
        foreach (var directory in Directory.EnumerateDirectories(fullPayloadDirectory, "*", SearchOption.AllDirectories))
        {
            GrantDirectory(directory, identity);
        }

        var payloadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPayloadFile(payloadFiles, executablePath, fullPayloadDirectory);
        var executableStem = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
            Path.GetFileNameWithoutExtension(executablePath));
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json", ".runtimeconfig.dev.json" })
        {
            AddPayloadFile(payloadFiles, executableStem + suffix, fullPayloadDirectory);
        }
        AddPayloadFile(payloadFiles, Path.Combine(fullPayloadDirectory, "pdfium.dll"), fullPayloadDirectory);

        var dependenciesPath = executableStem + ".deps.json";
        if (File.Exists(dependenciesPath))
        {
            using var dependencies = JsonDocument.Parse(File.ReadAllBytes(dependenciesPath));
            if (dependencies.RootElement.TryGetProperty("targets", out var targets))
            {
                foreach (var target in targets.EnumerateObject())
                {
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        foreach (var assetGroup in library.Value.EnumerateObject())
                        {
                            if (assetGroup.Name is not ("runtime" or "native" or "runtimeTargets" or "resources"))
                            {
                                continue;
                            }

                            foreach (var asset in assetGroup.Value.EnumerateObject())
                            {
                                var relativePath = asset.Value.TryGetProperty("localPath", out var localPath)
                                    ? localPath.GetString()
                                    : null;
                                relativePath ??= asset.Name;
                                AddPayloadFile(payloadFiles, Path.Combine(fullPayloadDirectory, relativePath), fullPayloadDirectory);
                                AddPayloadFile(
                                    payloadFiles,
                                    Path.Combine(fullPayloadDirectory, Path.GetFileName(relativePath)),
                                    fullPayloadDirectory);
                            }
                        }
                    }
                }
            }
        }

        foreach (var file in payloadFiles)
        {
            GrantFile(file, identity);
        }
    }

    private static bool HasInstallerProvisionedPayloadAccess(
        string payloadDirectory,
        IReadOnlyList<SecurityIdentifier> acceptedIdentities)
    {
        var security = new DirectoryInfo(payloadDirectory).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        foreach (var acceptedIdentity in acceptedIdentities)
        {
            var matching = rules
                .Where(rule => acceptedIdentity.Equals(rule.IdentityReference))
                .ToArray();
            if (matching.Any(rule => rule.AccessControlType == AccessControlType.Deny
                    && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) != 0))
            {
                continue;
            }

            var grantsRoot = matching.Any(rule =>
                rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0);
            var grantsChildren = matching.Any(rule =>
                rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
                && (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
            if (grantsRoot && grantsChildren)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProtectedInstallLocation(string path)
    {
        foreach (var specialFolder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.Windows
                 })
        {
            var protectedRoot = Environment.GetFolderPath(specialFolder);
            if (string.IsNullOrWhiteSpace(protectedRoot))
            {
                continue;
            }

            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot))
                + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddPayloadFile(HashSet<string> payloadFiles, string path, string payloadDirectory)
    {
        var fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        var payloadPrefix = Path.TrimEndingDirectorySeparator(payloadDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            return;
        }

        payloadFiles.Add(fullPath);
    }

    private static void GrantFile(string path, SecurityIdentifier identity)
    {
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            AccessControlType.Allow));
        fileInfo.SetAccessControl(security);
    }

    private static void GrantDirectory(string path, SecurityIdentifier identity)
    {
        var directoryInfo = new DirectoryInfo(path);
        var security = directoryInfo.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        directoryInfo.SetAccessControl(security);
    }

    private static SafeFileHandle CreateInheritedOutputSink()
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            InheritHandle = 1
        };
        var handle = CreateFileW(
            "NUL",
            GenericWrite,
            FileShareRead | FileShareWrite,
            ref securityAttributes,
            OpenExisting,
            0,
            nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create the PDF worker output sink.");
        }

        return handle;
    }

    private static StringBuilder BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(Quote(executablePath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(Quote(argument));
        }

        return commandLine;
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1)).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        result.Append('\\', checked(backslashes * 2)).Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
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
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    private sealed class ProcessThreadAttributeList : IDisposable
    {
        private nint _handle;
        private readonly List<nint> _values = [];

        public ProcessThreadAttributeList(int attributeCount)
        {
            nint size = nint.Zero;
            _ = InitializeProcThreadAttributeList(nint.Zero, attributeCount, 0, ref size);
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to size the PDF worker process attributes.");
            }

            _handle = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(_handle, attributeCount, 0, ref size))
            {
                var error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(_handle);
                _handle = nint.Zero;
                throw new Win32Exception(error, "Unable to initialize the PDF worker process attributes.");
            }
        }

        public nint DangerousGetHandle() => _handle;

        public void AddStructure<T>(nint attribute, T value)
            where T : struct
        {
            ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
            var size = Marshal.SizeOf<T>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
                Add(attribute, pointer, size);
                _values.Add(pointer);
            }
            catch
            {
                Marshal.FreeHGlobal(pointer);
                throw;
            }
        }

        public void AddUInt32(nint attribute, uint value)
        {
            ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
            var pointer = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(pointer, unchecked((int)value));
                Add(attribute, pointer, sizeof(uint));
                _values.Add(pointer);
            }
            catch
            {
                Marshal.FreeHGlobal(pointer);
                throw;
            }
        }

        public void AddHandles(nint attribute, ReadOnlySpan<nint> handles)
        {
            ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
            if (handles.IsEmpty)
            {
                throw new ArgumentException("At least one inherited handle is required.", nameof(handles));
            }

            var size = checked(handles.Length * nint.Size);
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                for (var index = 0; index < handles.Length; index++)
                {
                    Marshal.WriteIntPtr(pointer, checked(index * nint.Size), handles[index]);
                }

                Add(attribute, pointer, size);
                _values.Add(pointer);
            }
            catch
            {
                Marshal.FreeHGlobal(pointer);
                throw;
            }
        }

        private void Add(nint attribute, nint value, int size)
        {
            if (!UpdateProcThreadAttribute(_handle, 0, attribute, value, (nint)size, nint.Zero, nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure a PDF worker process attribute.");
            }
        }

        public void Dispose()
        {
            if (_handle == nint.Zero)
            {
                return;
            }

            DeleteProcThreadAttributeList(_handle);
            Marshal.FreeHGlobal(_handle);
            foreach (var value in _values)
            {
                Marshal.FreeHGlobal(value);
            }
            _values.Clear();
            _handle = nint.Zero;
        }
    }

    internal sealed class SafeSidHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSidHandle() : base(ownsHandle: true) { }
        public SafeSidHandle(nint value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() => FreeSid(handle) == nint.Zero;
    }

    [LibraryImport("userenv.dll", EntryPoint = "CreateAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        nint capabilities,
        uint capabilityCount,
        out nint appContainerSid);

    [LibraryImport("userenv.dll", EntryPoint = "DeriveAppContainerSidFromAppContainerName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string appContainerName, out nint appContainerSid);

    [DllImport("kernelbase.dll", EntryPoint = "GetAppContainerNamedObjectPath", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAppContainerNamedObjectPath(
        nint token,
        nint appContainerSid,
        uint objectPathLength,
        StringBuilder? objectPath,
        ref uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, uint flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nint attribute,
        nint value,
        nint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeKernelHandle process, uint exitCode);
}
