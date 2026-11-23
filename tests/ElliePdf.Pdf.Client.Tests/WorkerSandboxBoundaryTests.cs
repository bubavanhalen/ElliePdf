using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Pdf.Client.Tests;

public sealed class WorkerSandboxBoundaryTests
{
    private const uint TokenQuery = 0x0008;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_capability_appcontainer_denies_ambient_file_network_and_named_object_authority(bool lessPrivileged)
    {
        var boundaryDirectory = Path.Combine(Path.GetTempPath(), $"elliepdf-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(boundaryDirectory);
        var readPath = Path.Combine(boundaryDirectory, "ambient-secret.txt");
        var writePath = Path.Combine(boundaryDirectory, "worker-created.txt");
        await File.WriteAllTextAsync(readPath, "must remain outside the PDF worker sandbox");

        var mappingName = $"Local\\ElliePdf_Ambient_{Guid.NewGuid():N}";
        using var mapping = MemoryMappedFile.CreateNew(mappingName, 4_096, MemoryMappedFileAccess.ReadWrite);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var job = new WorkerJob(128L * 1024 * 1024, 80);
            using var launch = WorkerAppContainerProcess.Start(
                lessPrivileged ? SelfContainedWorkerExecutablePath() : WorkerExecutablePath(),
                [
                    "--sandbox-probe",
                    "--read-path", readPath,
                    "--write-path", writePath,
                    "--mapping", mappingName,
                    "--loopback-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                Path.GetDirectoryName(lessPrivileged ? SelfContainedWorkerExecutablePath() : WorkerExecutablePath())!,
                lessPrivileged);
            using var process = launch.Process;

            AssertCapabilityFreeAppContainerToken(process, lessPrivileged);
            job.Assign(process);
            launch.ResumeAfterContainment();
            launch.StandardInput.Dispose();

            // LPAC cold-start can spend several seconds loading a self-contained preview runtime,
            // especially while the other real-worker tests are running in parallel.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.False(File.Exists(writePath));
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
            File.Delete(readPath);
            File.Delete(writePath);
            Directory.Delete(boundaryDirectory);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Required_appcontainer_real_worker_round_trip_is_observable_and_fail_closed()
    {
        await using var client = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = WorkerExecutablePath(),
            StartupTimeout = TimeSpan.FromSeconds(30),
            DefaultOperationTimeout = TimeSpan.FromSeconds(20),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            RequireAppContainerSandbox = true
        });

        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);

        Assert.Equal(WorkerSandboxMode.AppContainer, client.ActiveSandboxMode);
        Assert.Equal(3, (await session.GetMetadataAsync(CancellationToken.None)).PageCount);
        AssertCapabilityFreeAppContainerToken(GetWorkerProcess(client));
    }

    [Fact(Timeout = 60_000)]
    public async Task Self_contained_worker_round_trips_in_zero_capability_lpac()
    {
        await using var client = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = SelfContainedWorkerExecutablePath(),
            StartupTimeout = TimeSpan.FromSeconds(15),
            DefaultOperationTimeout = TimeSpan.FromSeconds(20),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            RequireAppContainerSandbox = true,
            UseLessPrivilegedAppContainer = true
        });

        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);

        Assert.Equal(WorkerSandboxMode.LessPrivilegedAppContainer, client.ActiveSandboxMode);
        Assert.Equal(3, (await session.GetMetadataAsync(CancellationToken.None)).PageCount);
        AssertCapabilityFreeAppContainerToken(GetWorkerProcess(client), expectLessPrivileged: true);
    }

    [Fact]
    public void Broker_pipe_rejects_a_non_owner_security_principal()
    {
        var pipeName = $"ElliePdf-denial-{Guid.NewGuid():N}";
        using var server = WorkerAppContainerProcess.CreateBrokerPipeServer(pipeName);

        Assert.True(ImpersonateAnonymousToken(GetCurrentThread()));
        try
        {
            using var denied = CreateFileW(
                $"\\\\.\\pipe\\{pipeName}",
                GenericRead | GenericWrite,
                0,
                nint.Zero,
                OpenExisting,
                0,
                nint.Zero);
            var error = Marshal.GetLastPInvokeError();

            Assert.True(denied.IsInvalid);
            Assert.Equal(5, error); // ERROR_ACCESS_DENIED
        }
        finally
        {
            Assert.True(RevertToSelf());
        }
    }

    private static void AssertCapabilityFreeAppContainerToken(Process process, bool expectLessPrivileged = false)
    {
        Assert.True(OpenProcessToken(process.SafeHandle, TokenQuery, out var token));
        using (token)
        {
            Assert.Equal(1, QueryTokenInt32(token, TokenInformationClass.IsAppContainer));
            Assert.Equal(0u, QueryTokenGroupCount(token, TokenInformationClass.Capabilities));
        }
    }

    private static int QueryTokenInt32(SafeAccessTokenHandle token, TokenInformationClass informationClass)
    {
        var value = 0;
        Assert.True(GetTokenInformation(token, informationClass, ref value, sizeof(int), out var returnedLength));
        Assert.Equal(sizeof(int), returnedLength);
        return value;
    }

    private static uint QueryTokenGroupCount(SafeAccessTokenHandle token, TokenInformationClass informationClass)
    {
        _ = GetTokenInformation(token, informationClass, nint.Zero, 0, out var requiredLength);
        Assert.True(requiredLength >= sizeof(uint));
        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            Assert.True(GetTokenInformation(token, informationClass, buffer, requiredLength, out _));
            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Process GetWorkerProcess(PdfWorkerClient client)
        => (Process?)typeof(PdfWorkerClient)
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)
            ?? throw new InvalidOperationException("The worker process is unavailable.");

    private static string WorkerExecutablePath()
        => SelfContainedWorkerExecutablePath();

    private static string SelfContainedWorkerExecutablePath()
        => TestWorkerPayloadLocator.FindSelfContainedWorker();

    private static string Fixture(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "testdata", "generated", name);
        return File.Exists(path) ? path : throw new FileNotFoundException("Fixture not found.", path);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private enum TokenInformationClass
    {
        IsAppContainer = 29,
        Capabilities = 30
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateAnonymousToken(nint threadHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
