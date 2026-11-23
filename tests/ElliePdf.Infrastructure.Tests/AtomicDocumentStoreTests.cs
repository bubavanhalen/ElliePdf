using ElliePdf.Domain.Documents;
using ElliePdf.Domain.Storage;
using ElliePdf.Infrastructure.Storage;
using Xunit;

namespace ElliePdf.Infrastructure.Tests;

public sealed class AtomicDocumentStoreTests
{
    [Fact]
    public async Task New_file_is_committed_atomically_and_leaves_no_debris()
    {
        using var f = new Fixture();
        var result = await f.Store.CommitAsync(Request(f.Path), Write("new"), Validate("new"));
        Assert.False(result.ReplacedExistingFile);
        Assert.Equal("new", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Existing_file_is_replaced_and_old_content_is_not_retained()
    {
        using var f = new Fixture("old");
        var result = await f.Store.CommitAsync(Request(f.Path, await f.Version()), Write("new"), Validate("new"));
        Assert.True(result.ReplacedExistingFile);
        Assert.Equal("new", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Writer_failure_preserves_destination_and_cleans_temp()
    {
        using var f = new Fixture("old");
        var version = await f.Version();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.CommitAsync(
            Request(f.Path, version), ThrowWriter, Validate("new")));
        Assert.Equal("old", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Cancellation_before_commit_preserves_destination()
    {
        using var f = new Fixture("old");
        using var cts = new CancellationTokenSource();
        var version = await f.Version();
        f.Observer = new StageObserver(AtomicSaveStage.DestinationVersionReverified, cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.CommitAsync(
            Request(f.Path, version), Write("new"), Validate("new"), cts.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Post_commit_validation_failure_rolls_back_replacement()
    {
        using var f = new Fixture("old");
        var version = await f.Version();
        var validations = 0;
        AtomicFileValidator validator = (path, token) =>
        {
            if (Interlocked.Increment(ref validations) == 2)
                return ValueTask.FromException(new FormatException("bad"));
            return ValueTask.CompletedTask;
        };
        var exception = await Assert.ThrowsAsync<AtomicPostCommitValidationException>(() => f.Store.CommitAsync(
            Request(f.Path, version), Write("new"), validator));
        Assert.Equal(AtomicCommitOutcome.RolledBack, exception.Outcome);
        Assert.Equal("old", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task External_change_during_prepare_is_a_conflict()
    {
        using var f = new Fixture("old");
        var expected = await f.Version();
        AtomicStreamWriter writer = async (Stream stream, CancellationToken _) =>
        {
            await stream.WriteAsync("new"u8.ToArray());
            await File.WriteAllTextAsync(f.Path, "external");
        };
        await Assert.ThrowsAsync<AtomicSaveConflictException>(() => f.Store.CommitAsync(Request(f.Path, expected), writer, Validate("new")));
        Assert.Equal("external", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Fail_if_exists_rejects_existing_destination()
    {
        using var f = new Fixture("old");
        await Assert.ThrowsAsync<AtomicSaveConflictException>(() => f.Store.CommitAsync(
            Request(f.Path) with { FailIfDestinationExists = true }, Write("new"), Validate("new")));
        Assert.Equal("old", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Concurrent_saves_to_same_destination_are_serialized()
    {
        using var f = new Fixture();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Store.CommitAsync(Request(f.Path), async (s, _) =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            await s.WriteAsync("first"u8.ToArray());
        }, Validate("first"));
        await firstStarted.Task;
        var second = f.Store.CommitAsync(Request(f.Path), Write("second"), Validate("second"));
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        releaseFirst.SetResult();
        await first;
        await second;
        Assert.Equal("second", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Concurrent_saves_with_same_expected_version_commit_once_and_stale_save_conflicts()
    {
        using var f = new Fixture("old");
        var expected = await f.Version();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = f.Store.CommitAsync(Request(f.Path, expected), async (s, _) =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            await s.WriteAsync("first"u8.ToArray());
        }, Validate("first"));
        await firstStarted.Task;

        var second = f.Store.CommitAsync(Request(f.Path, expected), Write("second"), Validate("second"));
        releaseFirst.SetResult();

        var firstResult = await first;
        var stale = await Assert.ThrowsAsync<AtomicSaveConflictException>(() => second);
        Assert.Equal(AtomicCommitOutcome.NotCommitted, stale.Outcome);
        Assert.Equal(AtomicCommitOutcome.Committed, firstResult.Outcome);
        Assert.Equal("first", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Theory]
    [InlineData(AtomicSaveStage.DestinationLockAcquired)]
    [InlineData(AtomicSaveStage.DestinationVersionVerified)]
    [InlineData(AtomicSaveStage.TemporaryFileCreated)]
    [InlineData(AtomicSaveStage.TemporaryFileWritten)]
    [InlineData(AtomicSaveStage.TemporaryFileFlushed)]
    [InlineData(AtomicSaveStage.TemporaryFileValidated)]
    [InlineData(AtomicSaveStage.DestinationVersionReverified)]
    public async Task Cancellation_at_each_precommit_stage_preserves_original(AtomicSaveStage stage)
    {
        using var f = new Fixture("old");
        using var cts = new CancellationTokenSource();
        f.Observer = new StageObserver(stage, cts);
        var expected = await f.Version();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.CommitAsync(
            Request(f.Path, expected), Write("new"), Validate("new"), cts.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Observer_failure_at_every_stage_does_not_change_commit_authority()
    {
        using var f = new Fixture("old") { Observer = new ThrowingObserver() };
        var result = await f.Store.CommitAsync(
            Request(f.Path, await f.Version()), Write("new"), Validate("new"));

        Assert.Equal(AtomicCommitOutcome.Committed, result.Outcome);
        Assert.Equal("new", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    [Fact]
    public async Task Stable_capture_fails_when_file_is_exclusively_write_shared()
    {
        using var f = new Fixture("old");
        await using var held = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => f.Provider.TryCaptureAsync(f.Path).AsTask());
    }

    [Fact]
    public async Task Unsupported_destination_policy_rejects_before_transaction_files_are_created()
    {
        using var f = new Fixture("old", new RejectingDestinationPolicy());
        var expected = await f.Version();

        var exception = await Assert.ThrowsAsync<AtomicCommitNotSupportedException>(() => f.Store.CommitAsync(
            Request(f.Path, expected), Write("new"), Validate("new")));

        Assert.Equal(AtomicCommitOutcome.NotCommitted, exception.Outcome);
        Assert.Equal("old", await File.ReadAllTextAsync(f.Path));
        f.AssertNoDebris();
    }

    private static AtomicSaveRequest Request(string path, FileVersionStamp? version = null) =>
        new(path, new ContentRevision(1), version);

    private static AtomicStreamWriter Write(string value) => async (stream, token) =>
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value), token);

    private static ValueTask ThrowWriter(Stream _, CancellationToken __) =>
        ValueTask.FromException(new InvalidOperationException("writer"));

    private static AtomicFileValidator Validate(string expected) => async (path, token) =>
    {
        Assert.Equal(expected, await File.ReadAllTextAsync(path, token));
    };

    private sealed class Fixture : IDisposable
    {
        public Fixture(string? initial = null, IAtomicDestinationPolicy? destinationPolicy = null)
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "elliepdf-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "document.pdf");
            if (initial is not null) File.WriteAllText(Path, initial);
            Provider = new FileVersionStampProvider();
            Store = new AtomicDocumentStore(Provider, new ObserverProxy(this), destinationPolicy);
        }

        public string DirectoryPath { get; }
        public string Path { get; }
        public FileVersionStampProvider Provider { get; }
        public AtomicDocumentStore Store { get; }
        public IAtomicSaveObserver? Observer { get; set; }
        public ValueTask<FileVersionStamp?> Version() => Provider.TryCaptureAsync(Path);
        public void AssertNoDebris() => Assert.Empty(Directory.EnumerateFiles(DirectoryPath, ".*ellie-*"));
        public void Dispose() { try { Directory.Delete(DirectoryPath, true); } catch { } }

        private sealed class ObserverProxy(Fixture fixture) : IAtomicSaveObserver
        {
            public ValueTask OnStageAsync(AtomicSaveStage stage, string id, CancellationToken token) =>
                fixture.Observer?.OnStageAsync(stage, id, token) ?? ValueTask.CompletedTask;
        }
    }

    private sealed class StageObserver(AtomicSaveStage target, CancellationTokenSource source) : IAtomicSaveObserver
    {
        public ValueTask OnStageAsync(AtomicSaveStage stage, string _, CancellationToken __)
        {
            if (stage == target) source.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IAtomicSaveObserver
    {
        public ValueTask OnStageAsync(AtomicSaveStage _, string __, CancellationToken ___) =>
            ValueTask.FromException(new InvalidOperationException("telemetry failure"));
    }

    private sealed class RejectingDestinationPolicy : IAtomicDestinationPolicy
    {
        public void EnsureSupported(string _) => throw new AtomicCommitNotSupportedException(
            "test destination policy rejection", new IOException("unsupported test destination"));
    }
}
