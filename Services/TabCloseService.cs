namespace ElliePdf.Services;

public interface ITabCloseService
{
    Task<bool> TryCloseTabAsync(Guid tabId, CancellationToken cancellationToken = default);

    Task<bool> TryCloseAllDirtyTabsAsync(CancellationToken cancellationToken = default);
}

public sealed class TabCloseService : ITabCloseService
{
    private readonly IDocumentTabService _tabService;
    private readonly IAnnotationStore _annotationStore;
    private readonly IUnsavedChangesPrompt _unsavedChangesPrompt;
    private readonly IEditSaveService _editSaveService;
    private readonly SemaphoreSlim _closeGate = new(1, 1);

    public TabCloseService(
        IDocumentTabService tabService,
        IAnnotationStore annotationStore,
        IUnsavedChangesPrompt unsavedChangesPrompt,
        IEditSaveService editSaveService,
        IOverlayHistory history)
    {
        _tabService = tabService;
        _annotationStore = annotationStore;
        _unsavedChangesPrompt = unsavedChangesPrompt;
        _editSaveService = editSaveService;
        _history = history;
    }

    public async Task<bool> TryCloseTabAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var tab = _tabService.Tabs.FirstOrDefault(item => item.Id == tabId);
                if (tab is null)
                {
                    return true;
                }

                if (!tab.IsDirty)
                {
                    return await CloseTabCoreAsync(tab, cancellationToken).ConfigureAwait(false);
                }

                var choice = await _unsavedChangesPrompt
                    .PromptAsync(tab.DisplayName, cancellationToken)
                    .ConfigureAwait(false);
                switch (choice)
                {
                    case UnsavedChangesChoice.Cancel:
                        return false;

                    case UnsavedChangesChoice.Discard:
                        await _annotationStore.StopAndDeleteRecoveryAsync(
                                tab.Id,
                                tab.FilePath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return await CloseTabCoreAsync(tab, cancellationToken).ConfigureAwait(false);

                    case UnsavedChangesChoice.Save:
                        await _editSaveService
                            .SaveTabAsync(tab, tab.FilePath, cancellationToken)
                            .ConfigureAwait(false);
                        if (!tab.IsDirty)
                        {
                            return await CloseTabCoreAsync(tab, cancellationToken).ConfigureAwait(false);
                        }

                        // A newer revision appeared while the captured revision was saving.
                        // Loop and ask about that newer revision instead of silently closing it.
                        break;

                    default:
                        return false;
                }
            }
        }
        finally
        {
            _closeGate.Release();
        }
    }

    public async Task<bool> TryCloseAllDirtyTabsAsync(
        CancellationToken cancellationToken = default)
    {
        await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discardTabs = new Dictionary<Guid, DiscardDecision>();

            while (true)
            {
                var dirtyTabs = _tabService.Tabs
                    .Where(tab => tab.IsDirty && !discardTabs.ContainsKey(tab.Id))
                    .ToArray();
                if (dirtyTabs.Length == 0)
                {
                    var changedDiscards = discardTabs
                        .Where(pair => pair.Value.Tab.State.ContentRevision != pair.Value.DecidedRevision)
                        .Select(static pair => pair.Key)
                        .ToArray();
                    if (changedDiscards.Length == 0)
                    {
                        break;
                    }

                    foreach (var changedTabId in changedDiscards)
                    {
                        discardTabs.Remove(changedTabId);
                    }

                    continue;
                }

                foreach (var tab in dirtyTabs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var choice = await _unsavedChangesPrompt
                        .PromptAsync(tab.DisplayName, cancellationToken)
                        .ConfigureAwait(false);
                    if (choice == UnsavedChangesChoice.Cancel)
                    {
                        return false;
                    }

                    if (choice == UnsavedChangesChoice.Discard)
                    {
                        discardTabs[tab.Id] = new DiscardDecision(tab, tab.State.ContentRevision);
                        continue;
                    }

                    if (choice != UnsavedChangesChoice.Save)
                    {
                        return false;
                    }

                    await _editSaveService
                        .SaveTabAsync(tab, tab.FilePath, cancellationToken)
                        .ConfigureAwait(false);
                    // The outer loop re-prompts if an edit arrived during this save.
                }
            }

            // Defer destructive recovery cleanup until every prompt and save has
            // completed, so cancelling a later tab cannot partially discard an earlier one.
            foreach (var decision in discardTabs.Values)
            {
                var tab = decision.Tab;
                await _annotationStore.StopAndDeleteRecoveryAsync(
                        tab.Id,
                        tab.FilePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _annotationStore.RemoveTabAsync(tab.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Recheck after all asynchronous work. Any newly dirty tab must be
            // handled on another pass before the window is allowed to close.
            return !_tabService.Tabs.Any(tab => tab.IsDirty && !discardTabs.ContainsKey(tab.Id));
        }
        finally
        {
            _closeGate.Release();
        }
    }

    private async Task<bool> CloseTabCoreAsync(
        DocumentTab tab,
        CancellationToken cancellationToken)
    {
        await _tabService.CloseTabAsync(tab.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed record DiscardDecision(
        DocumentTab Tab,
        ElliePdf.Domain.Documents.ContentRevision DecidedRevision);
}
