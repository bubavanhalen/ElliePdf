namespace ElliePdf.Pdf.Client;

public sealed record PdfWorkerClientOptions
{
    public string WorkerExecutablePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "ElliePdf.Pdfium.Worker.exe");
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DefaultOperationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public long JobMemoryLimitBytes { get; init; } = 512L * 1024 * 1024;
    public uint CpuHardCapPercent { get; init; } = 80;
    /// <summary>
    /// Refuses to launch unless Windows creates a zero-capability AppContainer worker. Release
    /// builds default to fail closed; Debug builds may use the explicitly observable developer
    /// fallback modes.
    /// </summary>
    public bool RequireAppContainerSandbox { get; init; } = DefaultRequireAppContainerSandbox;
    /// <summary>
    /// Opts the AppContainer out of the broad All Application Packages group. The resulting LPAC
    /// is more restrictive and requires a self-contained worker payload with explicit ACL grants.
    /// </summary>
    public bool UseLessPrivilegedAppContainer { get; init; } = DefaultUseLessPrivilegedAppContainer;
    /// <summary>
    /// Refuses to launch when Windows cannot create the filtered-token worker. Enable this in
    /// packaged/release environments after validating the host's token policy.
    /// </summary>
    public bool RequireRestrictedTokenSandbox { get; init; }

#if DEBUG
    private const bool DefaultRequireAppContainerSandbox = false;
#else
    private const bool DefaultRequireAppContainerSandbox = true;
#endif
    // Capability-free AppContainer is the proven production default. LPAC remains an explicit
    // opt-in for packages whose installer provisions its stricter payload ACLs.
    private const bool DefaultUseLessPrivilegedAppContainer = false;

    internal PdfWorkerClientOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkerExecutablePath);
        if (!Path.IsPathFullyQualified(WorkerExecutablePath))
        {
            throw new ArgumentException("The worker executable path must be absolute.", nameof(WorkerExecutablePath));
        }

        if (StartupTimeout <= TimeSpan.Zero || StartupTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        if (DefaultOperationTimeout <= TimeSpan.Zero || DefaultOperationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(DefaultOperationTimeout));
        if (HeartbeatInterval < TimeSpan.FromMilliseconds(100) || HeartbeatInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
        if (HeartbeatTimeout < HeartbeatInterval || HeartbeatTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(HeartbeatTimeout));
        if (JobMemoryLimitBytes < 64L * 1024 * 1024 || JobMemoryLimitBytes > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(JobMemoryLimitBytes));
        if (CpuHardCapPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(CpuHardCapPercent));
        return this;
    }
}

public enum WorkerSandboxMode
{
    LessPrivilegedAppContainer,
    AppContainer,
    RestrictedToken,
    JobConstrainedCompatibility
}

public sealed class PdfWorkerRemoteException : IOException
{
    public PdfWorkerRemoteException(string code, string message, bool isTransient)
        : base(message)
    {
        Code = code;
        IsTransient = isTransient;
    }

    public string Code { get; }
    public bool IsTransient { get; }
}

public sealed class PdfWorkerQuarantinedException : InvalidOperationException
{
    public PdfWorkerQuarantinedException() : base("This document was quarantined after repeated worker failures.") { }
}

public sealed class PdfWorkerUnavailableException : IOException
{
    public PdfWorkerUnavailableException(string message, Exception? innerException = null) : base(message, innerException) { }
}
