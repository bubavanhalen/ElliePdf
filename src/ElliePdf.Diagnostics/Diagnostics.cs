using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElliePdf.Diagnostics;

public enum CrashDumpMode { None, Mini, Full }

public sealed record CrashUploadPolicy(bool OptedIn, CrashDumpMode DumpMode)
{
    public bool IsAllowed => OptedIn && DumpMode == CrashDumpMode.Mini;
    public static CrashUploadPolicy Disabled => new(false, CrashDumpMode.None);
}

public sealed record DiagnosticEvent(string Category, string Message, IReadOnlyDictionary<string, object?>? Properties = null);
public sealed record SupportBundlePreview(long EventCount, long Bytes, DateTimeOffset Oldest, DateTimeOffset Newest, bool ContainsSensitiveData);

public sealed class PrivacySafeDiagnostics : IDisposable
{
    public const long MaxLogBytes = 20L * 1024 * 1024;
    public const int MaxMessageCharacters = 4096;
    public const int MaxPropertyCharacters = 1024;
    public const int MaxProperties = 64;
    public static readonly TimeSpan MaxLogAge = TimeSpan.FromDays(7);
    private static readonly Regex PathPattern = new(@"(?:[A-Za-z]:[\\/]|\\\\|/)(?:[^\s\\/]+[\\/])*[^\s]+", RegexOptions.Compiled);
    private static readonly Regex PdfFileName = new(
        @"(?i)(?<![A-Za-z0-9._-])[A-Za-z0-9][A-Za-z0-9 _().-]{0,127}\.pdf(?![A-Za-z0-9._-])",
        RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(password|passphrase|secret|token|content|document(name)?|file(name)?|path|uri|url)\s*[:=]\s*[^,;\s]+", RegexOptions.Compiled);
    private readonly string _directory;
    private readonly string _file;
    private readonly object _gate = new();
    private bool _disposed;

    public PrivacySafeDiagnostics(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "events.jsonl");
        Prune();
    }

    public string LogPath => _file;

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var safe = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["category"] = Clean(diagnosticEvent.Category, 64),
            ["message"] = Clean(diagnosticEvent.Message, MaxMessageCharacters)
        };
        if (diagnosticEvent.Properties is not null)
            foreach (var pair in diagnosticEvent.Properties.Take(MaxProperties))
                safe[SafeKey(pair.Key)] = IsSensitiveKey(pair.Key) ? "[redacted]" : CleanValue(pair.Value);
        var line = JsonSerializer.Serialize(
            safe,
            DiagnosticsJsonContext.Default.DiagnosticProperties) + Environment.NewLine;
        lock (_gate)
        {
            File.AppendAllText(_file, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Prune();
        }
    }

    public SupportBundlePreview Preview()
    {
        lock (_gate)
        {
            Prune();
            long count = 0, bytes = 0; DateTimeOffset oldest = default, newest = default;
            foreach (var line in File.Exists(_file) ? File.ReadLines(_file) : [])
            {
                if (line.Length == 0) continue;
                count++; bytes += Encoding.UTF8.GetByteCount(line) + 1;
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("timestamp", out var stamp) && DateTimeOffset.TryParse(stamp.GetString(), out var time))
                { if (oldest == default || time < oldest) oldest = time; if (time > newest) newest = time; }
            }
            return new(count, bytes, oldest, newest, false);
        }
    }

    public string ExportSupportBundle()
    {
        var target = Path.Combine(_directory, $"support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}.json");
        ExportSupportBundle(target);
        return target;
    }

    public void ExportSupportBundle(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var target = Path.GetFullPath(destinationPath);
        lock (_gate)
        {
            Prune();
            var bundle = new SupportBundleDocument(
                Schema: 1,
                GeneratedAt: DateTimeOffset.UtcNow,
                Events: File.Exists(_file) ? File.ReadAllLines(_file) : []);
            var parent = Path.GetDirectoryName(target)
                ?? throw new ArgumentException("The support bundle destination requires a parent directory.", nameof(destinationPath));
            Directory.CreateDirectory(parent);
            File.WriteAllText(
                target,
                JsonSerializer.Serialize(bundle, DiagnosticsJsonContext.Default.SupportBundleDocument),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public void DeleteLocalData()
    {
        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json*")) File.Delete(file);
        }
    }

    public static bool IsCrashUploadAllowed(CrashUploadPolicy policy) => policy.IsAllowed;

    private static string SafeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Regex.Replace(key.Length > 64 ? key[..64] : key, "[^A-Za-z0-9_.-]", "_");
    }
    private static bool IsSensitiveKey(string key) => Regex.IsMatch(key, "(?i)(password|passphrase|secret|token|content|document(name)?|file(name)?|path|uri|url)");
    private static object? CleanValue(object? value) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        string text => Clean(text, MaxPropertyCharacters),
        IEnumerable<string> list => list.Take(64).Select(static item => Clean(item, MaxPropertyCharacters)).ToArray(),
        _ => Clean(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, MaxPropertyCharacters)
    };
    private static string Clean(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bounded = value.Length > maximumCharacters ? value[..maximumCharacters] : value;
        return Secret.Replace(
            PdfFileName.Replace(PathPattern.Replace(bounded, "[redacted]"), "[redacted]"),
            "$1=[redacted]");
    }
    private void Prune()
    {
        if (!File.Exists(_file)) return;
        var cutoff = DateTimeOffset.UtcNow - MaxLogAge;
        var lines = File.ReadAllLines(_file).Where(line => TryTimestamp(line, out var t) && t >= cutoff).ToList();
        var newlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
        long bytes = lines.Sum(line => (long)Encoding.UTF8.GetByteCount(line) + newlineBytes);
        while (bytes > MaxLogBytes && lines.Count > 0)
        {
            bytes -= Encoding.UTF8.GetByteCount(lines[0]) + newlineBytes;
            lines.RemoveAt(0);
        }
        File.WriteAllLines(_file, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
    private static bool TryTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try { using var json = JsonDocument.Parse(line); return json.RootElement.TryGetProperty("timestamp", out var value) && DateTimeOffset.TryParse(value.GetString(), out timestamp); }
        catch (JsonException) { return false; }
    }
    public void Dispose() { _disposed = true; }
}
