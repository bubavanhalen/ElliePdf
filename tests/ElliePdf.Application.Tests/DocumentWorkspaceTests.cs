using ElliePdf.Domain.Documents;
using Xunit;

namespace ElliePdf.Application.Tests;

public sealed class DocumentWorkspaceTests
{
    [Fact]
    public async Task Two_workspaces_have_independent_tabs_and_active_documents()
    {
        var factory = new TestSessionFactory();
        await using var first = new DocumentWorkspace(factory);
        await using var second = new DocumentWorkspace(factory);

        DocumentContext firstContext = await first.OpenOrActivateAsync("docs\\one.pdf");
        DocumentContext secondContext = await second.OpenOrActivateAsync("docs\\one.pdf");

        Assert.NotSame(firstContext, secondContext);
        Assert.NotEqual(firstContext.Id, secondContext.Id);
        Assert.Single(first.Documents);
        Assert.Single(second.Documents);

        Assert.True(await first.CloseAsync(firstContext.Id));
        Assert.Empty(first.Documents);
        Assert.Same(secondContext, second.ActiveDocument);
        Assert.Single(second.Documents);
    }

    [Fact]
    public async Task Concurrent_open_of_same_canonical_path_creates_one_tab()
    {
        var factory = new TestSessionFactory { OpenDelay = TimeSpan.FromMilliseconds(20) };
        await using var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext>[] opens = Enumerable.Range(0, 12)
            .Select(_ => workspace.OpenOrActivateAsync("docs\\sub\\..\\one.pdf").AsTask())
            .ToArray();

        DocumentContext[] contexts = await Task.WhenAll(opens);

        Assert.Single(workspace.Documents);
        Assert.All(contexts, context => Assert.Same(contexts[0], context));
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task Different_documents_can_open_while_another_open_is_blocked()
    {
        var factory = new TestSessionFactory { BlockPath = "one.pdf" };
        await using var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext> blocked = workspace.OpenOrActivateAsync("one.pdf").AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        DocumentContext second = await workspace.OpenOrActivateAsync("two.pdf")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("two.pdf", second.Snapshot.DisplayName);
        factory.AllowBlockedOpen();
        await blocked;
    }

    [Fact]
    public async Task Cancelling_one_duplicate_waiter_does_not_cancel_shared_open()
    {
        var factory = new TestSessionFactory { BlockPath = "one.pdf" };
        await using var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext> first = workspace.OpenOrActivateAsync("one.pdf").AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        Task<DocumentContext> cancelled = workspace
            .OpenOrActivateAsync(".\\one.pdf", cancellationToken: cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        factory.AllowBlockedOpen();
        DocumentContext context = await first;
        Assert.Same(context, workspace.Documents.Single());
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task Dispose_cancels_and_observes_an_in_flight_open()
    {
        var factory = new TestSessionFactory { BlockUntilCancelled = true };
        var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext> open = workspace.OpenOrActivateAsync("one.pdf").AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await workspace.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Empty(workspace.Documents);
    }

    [Fact]
    public async Task Session_factory_is_not_invoked_while_workspace_gate_is_held()
    {
        var factory = new TestSessionFactory();
        await using var workspace = new DocumentWorkspace(factory);
        factory.OnOpen = () => Assert.Empty(workspace.Documents);

        await workspace.OpenOrActivateAsync("one.pdf").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelling_the_only_waiter_cancels_the_pending_open_without_adding_a_tab()
    {
        var factory = new TestSessionFactory { BlockUntilCancelled = true };
        await using var workspace = new DocumentWorkspace(factory);
        using var cancellation = new CancellationTokenSource();
        Task<DocumentContext> open = workspace
            .OpenOrActivateAsync("one.pdf", cancellationToken: cancellation.Token).AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        await factory.OpenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(workspace.Documents);
    }

    [Fact]
    public async Task Attach_racing_a_pending_open_converges_on_the_attached_context()
    {
        var factory = new TestSessionFactory { BlockPath = "one.pdf" };
        await using var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext> pending = workspace.OpenOrActivateAsync("one.pdf").AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var attachedId = DocumentId.New();
        var attachedSession = new TestSession(documentId: attachedId);
        var request = new DocumentOpenRequest(attachedId, "one.pdf", "one.pdf");

        DocumentContext attached = await workspace.AttachOrActivateAsync(request, attachedSession);
        factory.AllowBlockedOpen();
        DocumentContext opened = await pending;

        Assert.Same(attached, opened);
        Assert.Single(workspace.Documents);
        Assert.False(attachedSession.Disposed);
    }

    [Fact]
    public async Task Retry_after_failed_open_uses_a_new_pending_operation()
    {
        var factory = new TestSessionFactory { FailFirstOpen = true };
        await using var workspace = new DocumentWorkspace(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.OpenOrActivateAsync("one.pdf").AsTask());
        DocumentContext context = await workspace.OpenOrActivateAsync("one.pdf");

        Assert.Same(context, workspace.Documents.Single());
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task Factory_ignoring_cancellation_cannot_add_a_hidden_document()
    {
        var redundantSession = new TestSession();
        var factory = new TestSessionFactory(redundantSession)
        {
            BlockPath = "one.pdf",
            IgnoreCancellation = true
        };
        await using var workspace = new DocumentWorkspace(factory);
        using var cancellation = new CancellationTokenSource();
        Task<DocumentContext> open = workspace
            .OpenOrActivateAsync("one.pdf", cancellationToken: cancellation.Token).AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        factory.AllowBlockedOpen();
        await redundantSession.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(redundantSession.Disposed);
        Assert.Empty(workspace.Documents);
    }

    [Fact]
    public async Task Workspace_dispose_waits_for_redundant_session_cleanup()
    {
        var redundantSession = new TestSession(blockDisposal: true);
        var factory = new TestSessionFactory(redundantSession) { BlockPath = "one.pdf" };
        var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext> pending = workspace.OpenOrActivateAsync("one.pdf").AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var attachedId = DocumentId.New();
        var attachedSession = new TestSession(documentId: attachedId);
        await workspace.AttachOrActivateAsync(
            new DocumentOpenRequest(attachedId, "one.pdf", "one.pdf"), attachedSession);
        factory.AllowBlockedOpen();
        await pending;
        await redundantSession.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = workspace.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        redundantSession.AllowDisposal();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Workspace_dispose_observes_redundant_session_cleanup_failure()
    {
        var redundantSession = new TestSession(throwOnDispose: true);
        var factory = new TestSessionFactory(redundantSession) { BlockPath = "one.pdf" };
        var workspace = new DocumentWorkspace(factory);
        Task<DocumentContext> pending = workspace.OpenOrActivateAsync("one.pdf").AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var attachedId = DocumentId.New();
        await workspace.AttachOrActivateAsync(
            new DocumentOpenRequest(attachedId, "one.pdf", "one.pdf"),
            new TestSession(documentId: attachedId));
        factory.AllowBlockedOpen();
        await pending;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => workspace.DisposeAsync().AsTask());
        Assert.Contains(failure.InnerExceptions,
            exception => exception.Message.Contains("Synthetic disposal failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_tracks_replaced_runner_until_its_cleanup_finishes()
    {
        var firstSession = new TestSession(blockDisposal: true);
        var factory = new TestSessionFactory
        {
            BlockPath = "one.pdf",
            IgnoreCancellation = true,
            SessionSelector = count => count == 1 ? firstSession : new TestSession()
        };
        var workspace = new DocumentWorkspace(factory);
        using var cancellation = new CancellationTokenSource();
        Task<DocumentContext> first = workspace
            .OpenOrActivateAsync("one.pdf", cancellationToken: cancellation.Token).AsTask();
        await factory.BlockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        factory.AllowBlockedOpen();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await firstSession.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await workspace.OpenOrActivateAsync("one.pdf");
        Task dispose = workspace.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        firstSession.AllowDisposal();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Activate_and_close_update_workspace_snapshot()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        DocumentContext one = await workspace.OpenOrActivateAsync("one.pdf");
        DocumentContext two = await workspace.OpenOrActivateAsync("two.pdf");

        Assert.True(await workspace.ActivateAsync(one.Id));
        Assert.Equal(one.Id, workspace.Snapshot.ActiveDocumentId);
        Assert.True(await workspace.CloseAsync(one.Id));
        Assert.Single(workspace.Documents);
        Assert.Equal(two.Id, workspace.ActiveDocument!.Id);
        Assert.False(await workspace.ActivateAsync(one.Id));
    }

    [Fact]
    public async Task Dispose_cancels_and_observes_operation_before_engine_session()
    {
        var session = new TestSession(requireOperationObservation: true);
        var factory = new TestSessionFactory(session);
        await using var workspace = new DocumentWorkspace(factory);
        DocumentContext context = await workspace.OpenOrActivateAsync("one.pdf");
        Task operation = context.RunRenderAsync(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                session.MarkOperationCancelled();
                throw;
            }
        });

        await context.DisposeAsync();

        Assert.True(session.OperationObservedCancellation);
        Assert.True(session.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
    }

    [Fact]
    public async Task Generations_cancel_previous_work()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        DocumentContext context = await workspace.OpenOrActivateAsync("one.pdf");
        Task operation = context.RunSearchAsync(async token => await Task.Delay(Timeout.InfiniteTimeSpan, token));

        SearchGeneration generation = context.AdvanceSearchGeneration();

        Assert.Equal(1, generation.Value);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
    }

    [Fact]
    public async Task State_commands_and_snapshot_remain_one_consistent_projection()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        DocumentContext context = await workspace.OpenOrActivateAsync("one.pdf");
        context.SetPageCount(3);

        context.MarkContentChanged();
        SaveOperation save = context.BeginSave();
        context.MarkContentChanged();
        context.MarkRecoveryCheckpointCompleted(save.CapturedRevision, succeeded: true);
        context.CompleteSave(save);

        DocumentState state = context.State;
        DocumentSnapshot snapshot = context.Snapshot;
        Assert.Equal(2, state.ContentRevision.Value);
        Assert.Equal(1, state.SavedRevision.Value);
        Assert.True(state.HasUnsavedChanges);
        Assert.Equal(RecoveryState.Pending, state.RecoveryState);
        Assert.Equal(state.ContentRevision, snapshot.ContentRevision);
        Assert.Equal(state.SavedRevision, snapshot.SavedRevision);
        Assert.Equal(state.StructureRevision, snapshot.StructureRevision);
        Assert.Equal(state.HasUnsavedChanges, snapshot.HasUnsavedChanges);
        Assert.Equal(state.RecoveryState, snapshot.RecoveryState);
        Assert.Equal(state.ExternalFileState, snapshot.ExternalFileState);
    }

    [Fact]
    public async Task Save_conflict_is_projected_to_the_snapshot()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        DocumentContext context = await workspace.OpenOrActivateAsync("one.pdf");
        context.MarkContentChanged();
        SaveOperation save = context.BeginSave();

        context.FailSave(save, SaveFailureKind.ExternalChanged, "external_changed");

        Assert.Equal(ExternalFileState.Changed, context.State.ExternalFileState);
        Assert.Equal(ExternalFileState.Changed, context.Snapshot.ExternalFileState);
        Assert.True(context.Snapshot.HasUnsavedChanges);
    }

    [Fact]
    public async Task Concurrent_context_dispose_waits_for_the_single_session_disposal()
    {
        var session = new TestSession(blockDisposal: true);
        await using var workspace = new DocumentWorkspace(new TestSessionFactory(session));
        DocumentContext context = await workspace.OpenOrActivateAsync("one.pdf");

        Task first = context.DisposeAsync().AsTask();
        await session.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = context.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);
        session.AllowDisposal();
        await Task.WhenAll(first, second);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Attach_adopts_ui_opened_session_and_preserves_requested_identity()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        var documentId = DocumentId.New();
        var session = new TestSession(documentId: documentId);
        var request = new DocumentOpenRequest(documentId, "one.pdf", "one.pdf");

        DocumentContext context = await workspace.AttachOrActivateAsync(request, session);

        Assert.Equal(documentId, context.Id);
        Assert.Same(context, workspace.ActiveDocument);
        Assert.True(await workspace.CloseAsync(documentId));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Cancelled_attach_disposes_the_unadopted_ui_session()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        var documentId = DocumentId.New();
        var session = new TestSession(documentId: documentId);
        var request = new DocumentOpenRequest(documentId, "one.pdf", "one.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace
            .AttachOrActivateAsync(request, session, cancellationToken: cancellation.Token)
            .AsTask());

        Assert.True(session.Disposed);
        Assert.Empty(workspace.Documents);
    }

    [Fact]
    public async Task Duplicate_attach_disposes_the_redundant_session()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        var firstId = DocumentId.New();
        var firstSession = new TestSession(documentId: firstId);
        var firstRequest = new DocumentOpenRequest(firstId, "one.pdf", "one.pdf");
        DocumentContext first = await workspace.AttachOrActivateAsync(firstRequest, firstSession);
        var duplicateId = DocumentId.New();
        var duplicateSession = new TestSession(documentId: duplicateId);
        var duplicateRequest = new DocumentOpenRequest(duplicateId, ".\\one.pdf", "one.pdf");

        DocumentContext duplicate = await workspace.AttachOrActivateAsync(
            duplicateRequest,
            duplicateSession);

        Assert.Same(first, duplicate);
        Assert.True(duplicateSession.Disposed);
        Assert.False(firstSession.Disposed);
        Assert.Single(workspace.Documents);
    }

    [Fact]
    public async Task Attach_to_disposed_workspace_disposes_the_unadopted_session()
    {
        var workspace = new DocumentWorkspace(new TestSessionFactory());
        await workspace.DisposeAsync();
        await workspace.DisposeAsync();
        var documentId = DocumentId.New();
        var session = new TestSession(documentId: documentId);
        var request = new DocumentOpenRequest(documentId, "one.pdf", "one.pdf");

        await Assert.ThrowsAsync<ObjectDisposedException>(() => workspace
            .AttachOrActivateAsync(request, session)
            .AsTask());

        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Open_without_activation_does_not_replace_active_document()
    {
        await using var workspace = new DocumentWorkspace(new TestSessionFactory());
        DocumentContext active = await workspace.OpenOrActivateAsync("one.pdf");

        DocumentContext background = await workspace.OpenOrActivateAsync("two.pdf", activate: false);

        Assert.NotEqual(active.Id, background.Id);
        Assert.Equal(active.Id, workspace.ActiveDocument!.Id);
    }

    private sealed class TestSessionFactory(TestSession? session = null) : IPdfEngineSessionFactory
    {
        private readonly TestSession? _session = session;
        private int _openCount;
        private readonly TaskCompletionSource _allowBlockedOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCount => Volatile.Read(ref _openCount);
        public TimeSpan OpenDelay { get; init; }
        public string? BlockPath { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public bool FailFirstOpen { get; init; }
        public bool IgnoreCancellation { get; init; }
        public TaskCompletionSource BlockedOpenStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnOpen { get; set; }
        public Func<int, TestSession>? SessionSelector { get; init; }

        public void AllowBlockedOpen() => _allowBlockedOpen.TrySetResult();

        public async ValueTask<IPdfEngineSession> OpenAsync(DocumentOpenRequest request,
            CancellationToken cancellationToken)
        {
            int openNumber = Interlocked.Increment(ref _openCount);
            OnOpen?.Invoke();
            if (FailFirstOpen && OpenCount == 1)
            {
                throw new InvalidOperationException("Synthetic open failure.");
            }
            if (BlockUntilCancelled || string.Equals(Path.GetFileName(request.CanonicalPath), BlockPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                BlockedOpenStarted.TrySetResult();
                if (BlockUntilCancelled)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        OpenCancelled.TrySetResult();
                        throw;
                    }
                }
                if (IgnoreCancellation)
                {
                    await _allowBlockedOpen.Task;
                }
                else
                {
                    await _allowBlockedOpen.Task.WaitAsync(cancellationToken);
                }
            }
            if (OpenDelay > TimeSpan.Zero)
            {
                await Task.Delay(OpenDelay, cancellationToken);
            }
            return SessionSelector?.Invoke(openNumber) ?? _session ?? new TestSession();
        }
    }

    private sealed class TestSession : IPdfEngineSession
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _requireOperationObservation;
        private readonly bool _blockDisposal;
        private readonly bool _throwOnDispose;
        private readonly TaskCompletionSource _allowDisposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TestSession(
            bool requireOperationObservation = false,
            DocumentId? documentId = null,
            bool blockDisposal = false,
            bool throwOnDispose = false)
        {
            _requireOperationObservation = requireOperationObservation;
            _blockDisposal = blockDisposal;
            _throwOnDispose = throwOnDispose;
            DocumentId = documentId ?? DocumentId.New();
        }
        public bool OperationObservedCancellation => _cancelled.Task.IsCompleted;
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource DisposalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DocumentId DocumentId { get; }

        public void MarkOperationCancelled() => _cancelled.TrySetResult();

        public void AllowDisposal() => _allowDisposal.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposalStarted.TrySetResult();
            if (_blockDisposal)
            {
                await _allowDisposal.Task;
            }
            if (_requireOperationObservation)
            {
                Assert.True(OperationObservedCancellation);
            }
            if (_throwOnDispose)
            {
                throw new InvalidOperationException("Synthetic disposal failure.");
            }
            Disposed = true;
            await Task.CompletedTask;
        }
    }
}
