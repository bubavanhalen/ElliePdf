using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElliePdf;

/// <summary>Privacy controls for local navigation state. These controls never affect document content.</summary>
public sealed record SessionPrivacyPolicy(
    bool ReopenLastSession = true,
    bool KeepRecentFiles = true,
    bool PersistViewState = true,
    bool PersistDiagnostics = false)
{
    public static SessionPrivacyPolicy PrivateByDefault { get; } = new(false, false, false, false);
}

public sealed record SessionTabState
{
    public required string Path { get; init; }
    public int PageIndex { get; init; }
    public double Zoom { get; init; } = 1;
    public string ZoomMode { get; init; } = "fitWidth";
    public string ViewMode { get; init; } = "continuous";
    public bool SidebarOpen { get; init; }
    public string SidebarMode { get; init; } = "thumbnails";
    public bool IsLockedPlaceholder { get; init; }
}

public sealed record SessionWindowState
{
    public double Width { get; init; }
    public double Height { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public bool IsMaximized { get; init; }
}

public sealed record SessionStateDocument
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<SessionTabState> Tabs { get; init; } = [];
    public IReadOnlyList<string> RecentFiles { get; init; } = [];
    public SessionWindowState? Window { get; init; }
    public string? ActiveTabPath { get; init; }
}

public interface ISessionStateStore
{
    ValueTask<SessionStateDocument> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(SessionStateDocument state, SessionPrivacyPolicy policy, CancellationToken cancellationToken = default);
    ValueTask ClearAsync(SessionDataKind kind, CancellationToken cancellationToken = default);
}

[Flags]
public enum SessionDataKind { ReopenState = 1, Recents = 2, ViewState = 4, Diagnostics = 8, Recovery = 16 }

/// <summary>Small, atomic, corruption-tolerant JSON store. It stores paths and UI state only.</summary>
public sealed class AtomicSessionStateStore : ISessionStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicSessionStateStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A state path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<SessionStateDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync(
                stream,
                SessionStateJsonContext.Default.SessionStateDocument,
                cancellationToken).ConfigureAwait(false);
            return state is { Version: SessionStateDocument.CurrentVersion } ? Sanitize(state) : new();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        finally { _gate.Release(); }
    }

    public async ValueTask SaveAsync(SessionStateDocument state, SessionPrivacyPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tabs = policy.ReopenLastSession
            ? policy.PersistViewState
                ? state.Tabs
                : state.Tabs.Select(static tab => tab with
                {
                    PageIndex = 0,
                    Zoom = 1,
                    ZoomMode = "fitWidth",
                    ViewMode = "continuous",
                    SidebarOpen = false,
                    SidebarMode = "thumbnails"
                }).ToArray()
            : [];
        var recents = policy.KeepRecentFiles ? state.RecentFiles : [];
        var sanitized = Sanitize(state with
        {
            Version = SessionStateDocument.CurrentVersion,
            SavedAtUtc = DateTimeOffset.UtcNow,
            Tabs = tabs,
            RecentFiles = recents,
            Window = policy.PersistViewState ? state.Window : null,
            ActiveTabPath = policy.ReopenLastSession ? state.ActiveTabPath : null
        });
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    sanitized,
                    SessionStateJsonContext.Default.SessionStateDocument,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, _path, true);
        }
        finally
        {
            _gate.Release();
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort cleanup */ }
        }
    }

    public async ValueTask ClearAsync(SessionDataKind kind, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken);
        if ((kind & SessionDataKind.ReopenState) != 0)
        {
            current = current with { Tabs = [], ActiveTabPath = null };
        }

        if ((kind & SessionDataKind.ViewState) != 0)
            current = current with
            {
                Window = null,
                Tabs = current.Tabs.Select(static tab => tab with
                {
                    PageIndex = 0,
                    Zoom = 1,
                    ZoomMode = "fitWidth",
                    ViewMode = "continuous",
                    SidebarOpen = false,
                    SidebarMode = "thumbnails"
                }).ToArray()
            };
        if ((kind & SessionDataKind.Recents) != 0) current = current with { RecentFiles = [] };
        await SaveAsync(current, new(), cancellationToken);
    }

    private static SessionStateDocument Sanitize(SessionStateDocument state) => state with
    {
        Tabs = state.Tabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.Path)
                && tab.Path.Length <= 32767
                && tab.PageIndex >= 0
                && double.IsFinite(tab.Zoom)
                && tab.Zoom is >= 0.1 and <= 64)
            .Select(static tab => tab with
            {
                ViewMode = tab.ViewMode is "single" or "continuous" ? tab.ViewMode : "continuous",
                ZoomMode = tab.ZoomMode is "fitWidth" or "fitPage" or "actualSize" or "custom"
                    ? tab.ZoomMode
                    : "fitWidth",
                SidebarMode = tab.SidebarMode is "thumbnails" or "outline" or "search" ? tab.SidebarMode : "thumbnails"
            })
            .Take(64)
            .ToArray(),
        RecentFiles = state.RecentFiles
            .Where(static path => !string.IsNullOrWhiteSpace(path) && path.Length <= 32767)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray(),
        Window = SanitizeWindow(state.Window),
        ActiveTabPath = state.ActiveTabPath is { Length: > 0 and <= 32767 }
            ? state.ActiveTabPath
            : null
    };

    private static SessionWindowState? SanitizeWindow(SessionWindowState? window)
    {
        if (window is null || !double.IsFinite(window.Width) || !double.IsFinite(window.Height)
            || window.Width is < 500 or > 32768 || window.Height is < 320 or > 32768
            || window.X is < -32768 or > 32768 || window.Y is < -32768 or > 32768)
        {
            return null;
        }

        return window;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionStateDocument))]
internal sealed partial class SessionStateJsonContext : JsonSerializerContext;
