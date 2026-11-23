using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ElliePdf.Pdf.Client.Tests;

internal static class TestWorkerPayloadLocator
{
    public static string FindSelfContainedWorker()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            var architecture => throw new PlatformNotSupportedException(
                $"Worker integration tests do not support {architecture}.")
        };
        var workerBin = Path.Combine(RepositoryRoot(), "src", "ElliePdf.Pdfium.Worker", "bin");
        var searchRoots = new[]
        {
            Path.Combine(workerBin, platform, configuration),
            Path.Combine(workerBin, configuration)
        };
        var matches = searchRoots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "ElliePdf.Pdfium.Worker.exe", SearchOption.AllDirectories))
            .Where(file =>
                File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "coreclr.dll"))
                && File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "pdfium.dll"))
                && PayloadMatchesCurrentBuild(Path.GetDirectoryName(file)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches.Length > 0
            ? matches[0]
            : throw new FileNotFoundException(
                $"A self-contained {platform} {configuration} worker matching the current test build was not found.",
                searchRoots[0]);
    }

    private static bool PayloadMatchesCurrentBuild(string candidateDirectory)
    {
        var requiredAssemblies = new[]
        {
            "ElliePdf.Pdfium.Worker.dll",
            "ElliePdf.Pdf.Transport.dll",
            "ElliePdf.Pdf.Contracts.dll"
        };
        return requiredAssemblies.All(fileName => FilesMatch(
            Path.Combine(candidateDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)));
    }

    private static bool FilesMatch(string candidate, string current)
    {
        if (!File.Exists(candidate) || !File.Exists(current))
        {
            return false;
        }

        using var candidateStream = File.OpenRead(candidate);
        using var currentStream = File.OpenRead(current);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(candidateStream),
            SHA256.HashData(currentStream));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
