using ElliePdf.Diagnostics;

namespace ElliePdf.Diagnostics.Tests;

public sealed class DiagnosticsTests
{
    [Fact] public void RedactsSensitiveValuesAndPaths()
    {
        var dir = Directory.CreateTempSubdirectory(); using var log = new PrivacySafeDiagnostics(dir.FullName);
        log.Write(new("reader", "opened C:\\Users\\Alice\\secret.pdf; retry Quarterly Results.pdf", new Dictionary<string, object?> { ["path"] = "C:\\Users\\Alice\\secret.pdf", ["password"] = "hunter2", ["page"] = 3 }));
        var text = File.ReadAllText(log.LogPath);
        Assert.DoesNotContain("Alice", text); Assert.DoesNotContain("hunter2", text); Assert.DoesNotContain("Quarterly Results", text); Assert.Contains("[redacted]", text); Assert.Contains("3", text);
    }
    [Fact] public void CrashUploadRequiresExplicitMiniDumpOptIn() { Assert.False(PrivacySafeDiagnostics.IsCrashUploadAllowed(CrashUploadPolicy.Disabled)); Assert.False(PrivacySafeDiagnostics.IsCrashUploadAllowed(new(true, CrashDumpMode.Full))); Assert.True(PrivacySafeDiagnostics.IsCrashUploadAllowed(new(true, CrashDumpMode.Mini))); }
    [Fact] public void PreviewExportAndDeleteAreLocal() { var dir = Directory.CreateTempSubdirectory(); using var log = new PrivacySafeDiagnostics(dir.FullName); log.Write(new("test", "ok")); Assert.Equal(1, log.Preview().EventCount); var bundle = log.ExportSupportBundle(); Assert.True(File.Exists(bundle)); log.DeleteLocalData(); Assert.Empty(Directory.EnumerateFiles(dir.FullName)); }
}
