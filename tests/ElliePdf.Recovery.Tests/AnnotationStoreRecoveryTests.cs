using System.Text.Json;
using ElliePdf.Domain.Documents;
using ElliePdf.Domain.Storage;
using ElliePdf.Infrastructure.Storage;
using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Recovery.Tests;

public sealed class AnnotationStoreRecoveryTests : IAsyncLifetime
{
    private string _root = null!;
    private string _sourcePath = null!;
    private Guid _tabId;
    private FileVersionStampProvider _versions = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "ElliePdf-RecoveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sourcePath = Path.Combine(_root, "document.pdf");
        File.WriteAllBytes(_sourcePath, [37, 80, 68, 70, 45, 49]);
        _tabId = Guid.NewGuid();
        _versions = new FileVersionStampProvider();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CaptureOverlayDocumentIsDeepSnapshot()
    {
        var store = CreateStore(new RecordingAtomicStore());
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        var state = Overlay("original");
        store.SetPageOverlay(_tabId, 2, state, new ContentRevision(1));

        var snapshot = store.CaptureOverlayDocument(_tabId)!;
        snapshot.Pages[2].TextItems[0].Text = "changed";
        state.TextItems[0].Text = "also changed";

        Assert.Equal("original", store.GetPageOverlay(_tabId, 2).TextItems[0].Text);
    }

    [Fact]
    public async Task Form_edits_are_coalesced_and_round_trip_without_worker_session_ids()
    {
        var atomic = new RecordingAtomicStore();
        var store = CreateStore(atomic);
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        store.SetFormRecoveryEdit(_tabId, FormEdit("old"), new ContentRevision(1));
        store.SetFormRecoveryEdit(_tabId, FormEdit("new"), new ContentRevision(2));

        Assert.Single(store.GetFormRecoveryEdits(_tabId));
        await store.SaveRecoveryCheckpointAsync(_tabId, _sourcePath, new ContentRevision(2));

        var reopened = CreateStore(new RecordingAtomicStore());
        var reopenedTab = Guid.NewGuid();
        Assert.True(await reopened.LoadRecoveryAsync(reopenedTab, _sourcePath));
        var recovered = Assert.Single(reopened.GetFormRecoveryEdits(reopenedTab));
        Assert.Equal("new", recovered.Text);
        Assert.Equal("Name", recovered.FieldName);
    }

    [Fact]
    public async Task OlderCheckpointCannotClearPendingNewerRevision()
    {
        var atomic = new BlockingAtomicStore();
        var store = CreateStore(atomic);
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        store.SetPageOverlay(_tabId, 1, Overlay("v1"), new ContentRevision(1));
        var save = store.SaveRecoveryCheckpointAsync(_tabId, _sourcePath, new ContentRevision(1));
        await atomic.WriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.SetPageOverlay(_tabId, 1, Overlay("v2"), new ContentRevision(2));
        atomic.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.True(store.HasPendingCheckpoint(_tabId));
    }

    [Fact]
    public async Task StopAndDeleteRecoveryCancelsDelayedCheckpointWithoutRecreation()
    {
        var atomic = new RecordingAtomicStore();
        var store = CreateStore(atomic);
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        store.SetPageOverlay(_tabId, 1, Overlay("pending"), new ContentRevision(1));
        var version = await _versions.TryCaptureAsync(_sourcePath) ?? throw new InvalidOperationException();
        store.ScheduleRecoveryCheckpoint(_tabId, _sourcePath, new ContentRevision(1), version);

        await store.StopAndDeleteRecoveryAsync(_tabId, _sourcePath);
        await Task.Delay(1000);

        Assert.Empty(atomic.Commits);
        Assert.False(store.HasPendingCheckpoint(_tabId));
    }

    [Fact]
    public async Task Committed_snapshot_is_subtracted_without_losing_newer_edits()
    {
        var store = CreateStore(new RecordingAtomicStore());
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        var persisted = Overlay("persisted");
        persisted.TextItems[0].Id = "persisted-id";
        store.SetPageOverlay(_tabId, 0, persisted, new ContentRevision(1));
        store.SetFormRecoveryEdit(_tabId, FormEdit("persisted form"), new ContentRevision(1));
        var saveSnapshot = store.CaptureOverlayDocument(_tabId)!;

        var current = Overlay("persisted");
        current.TextItems[0].Id = "persisted-id";
        current.TextItems.Add(new TextOverlay { Id = "new-id", Text = "newer" });
        store.SetPageOverlay(_tabId, 0, current, new ContentRevision(2));
        store.SetFormRecoveryEdit(_tabId, FormEdit("newer form"), new ContentRevision(2));

        await store.CommitPersistedEditsAsync(
            _tabId,
            saveSnapshot,
            new ContentRevision(2));

        var remaining = store.CaptureOverlayDocument(_tabId)!;
        Assert.Equal("newer", Assert.Single(remaining.Pages[0].TextItems).Text);
        Assert.Equal("newer form", Assert.Single(remaining.FormEdits).Text);
        Assert.True(store.HasPendingCheckpoint(_tabId));
    }

    [Fact]
    public async Task Committed_snapshot_clears_recovery_when_no_newer_edit_exists()
    {
        var store = CreateStore(new RecordingAtomicStore());
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        var persisted = Overlay("persisted");
        persisted.TextItems[0].Id = "persisted-id";
        store.SetPageOverlay(_tabId, 0, persisted, new ContentRevision(1));
        var saveSnapshot = store.CaptureOverlayDocument(_tabId)!;

        await store.CommitPersistedEditsAsync(
            _tabId,
            saveSnapshot,
            new ContentRevision(1));

        Assert.Empty(store.CaptureOverlayDocument(_tabId)!.Pages);
        Assert.False(store.HasPendingCheckpoint(_tabId));
    }

    [Fact]
    public async Task CorruptRecoveryIsQuarantinedAndOpeningContinues()
    {
        var atomic = new RecordingAtomicStore();
        var store = CreateStore(atomic);
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        store.SetPageOverlay(_tabId, 1, Overlay("valid"), new ContentRevision(1));
        await store.SaveRecoveryCheckpointAsync(_tabId, _sourcePath, new ContentRevision(1));
        var recoveryPath = atomic.LastDestination!;
        await File.WriteAllTextAsync(recoveryPath, "not-json");

        var reopened = CreateStore(new RecordingAtomicStore());
        Assert.False(await reopened.LoadRecoveryAsync(Guid.NewGuid(), _sourcePath));
        Assert.False(File.Exists(recoveryPath));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(recoveryPath)!, Path.GetFileName(recoveryPath) + ".corrupt-*"));
    }

    [Fact]
    public async Task SourceFingerprintMismatchIsNotApplied()
    {
        var atomic = new RecordingAtomicStore();
        var store = CreateStore(atomic);
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        store.SetPageOverlay(_tabId, 1, Overlay("old source"), new ContentRevision(1));
        await store.SaveRecoveryCheckpointAsync(_tabId, _sourcePath, new ContentRevision(1));
        File.AppendAllText(_sourcePath, "changed");

        var reopened = CreateStore(new RecordingAtomicStore());
        var reopenedTab = Guid.NewGuid();
        Assert.False(await reopened.LoadRecoveryAsync(reopenedTab, _sourcePath));
        Assert.Empty(reopened.CaptureOverlayDocument(reopenedTab)?.Pages ?? []);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("schema")]
    public async Task PayloadChecksumAndSchemaAreValidatedAndQuarantined(string corruption)
    {
        var atomic = new RecordingAtomicStore();
        var store = CreateStore(atomic);
        await store.LoadRecoveryAsync(_tabId, _sourcePath);
        store.SetPageOverlay(_tabId, 1, Overlay("checksum"), new ContentRevision(1));
        await store.SaveRecoveryCheckpointAsync(_tabId, _sourcePath, new ContentRevision(1));
        var path = atomic.LastDestination!;
        var json = await File.ReadAllTextAsync(path);
        json = corruption == "checksum"
            ? json.Replace("\"PayloadSha256\":\"", "\"PayloadSha256\":\"BAD", StringComparison.Ordinal)
            : json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        var reopened = CreateStore(new RecordingAtomicStore());
        Assert.False(await reopened.LoadRecoveryAsync(Guid.NewGuid(), _sourcePath));
        Assert.False(File.Exists(path));
    }

    private AnnotationStore CreateStore(IAtomicDocumentStore atomic) =>
        new(new TestSettings(), atomic, _versions);

    private static PageOverlayState Overlay(string text) => new()
    {
        TextItems = [new TextOverlay { Text = text }]
    };

    private static FormRecoveryEdit FormEdit(string value) => new()
    {
        PageIndex = 0,
        FieldName = "Name",
        WidgetType = "Text",
        ValueKind = "Text",
        Text = value
    };

    private sealed class TestSettings : IUserSettingsService
    {
        public UserSettings Settings { get; } = new() { AutoSaveCompanion = true };
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class RecordingAtomicStore : IAtomicDocumentStore
    {
        public List<string> Commits { get; } = [];
        public string? LastDestination { get; private set; }

        public virtual async Task<AtomicCommitResult> CommitAsync(AtomicSaveRequest request, AtomicStreamWriter writer, AtomicFileValidator validator, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            var candidate = request.DestinationPath + ".candidate-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                await writer(stream, cancellationToken);
            await validator(candidate, cancellationToken);
            File.Move(candidate, request.DestinationPath, true);
            Commits.Add(request.DestinationPath);
            LastDestination = request.DestinationPath;
            var stamp = await new FileVersionStampProvider().TryCaptureAsync(request.DestinationPath) ?? throw new InvalidOperationException();
            return new AtomicCommitResult(request.DestinationPath, request.CapturedRevision, stamp, true);
        }
    }

    private sealed class BlockingAtomicStore : RecordingAtomicStore
    {
        public TaskCompletionSource<bool> WriterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult(true);

        public override async Task<AtomicCommitResult> CommitAsync(AtomicSaveRequest request, AtomicStreamWriter writer, AtomicFileValidator validator, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            var candidate = request.DestinationPath + ".candidate-" + Guid.NewGuid().ToString("N");
            await using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            WriterStarted.TrySetResult(true);
            await _release.Task;
            await writer(stream, cancellationToken);
            await validator(candidate, cancellationToken);
            File.Move(candidate, request.DestinationPath, true);
            return new AtomicCommitResult(request.DestinationPath, request.CapturedRevision,
                await new FileVersionStampProvider().TryCaptureAsync(request.DestinationPath) ?? throw new InvalidOperationException(), true);
        }
    }
}
