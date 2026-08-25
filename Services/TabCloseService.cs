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
    private readonly IOverlayHistory _history;

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

    public async Task<bool> TryCloseTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        var tab = _tabService.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null)
        {
            return true;
        }

        if (!_annotationStore.IsTabDirty(tabId))
        {
            return await CloseTabCoreAsync(tabId, cancellationToken);
        }

        var choice = await _unsavedChangesPrompt.PromptAsync(tab.DisplayName, cancellationToken);
        return choice switch
        {
            UnsavedChangesChoice.Cancel => false,
            UnsavedChangesChoice.Discard => await CloseTabCoreAsync(tabId, cancellationToken),
            UnsavedChangesChoice.Save => await SaveAndCloseTabCoreAsync(tab, cancellationToken),
            _ => false
        };
    }

    public async Task<bool> TryCloseAllDirtyTabsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var tab in _tabService.Tabs.Where(tab => _annotationStore.IsTabDirty(tab.Id)).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var choice = await _unsavedChangesPrompt.PromptAsync(tab.DisplayName, cancellationToken);
            if (choice == UnsavedChangesChoice.Cancel)
            {
                return false;
            }

            if (choice == UnsavedChangesChoice.Save)
            {
                await _editSaveService.SaveTabAsync(tab, tab.FilePath, cancellationToken);
            }
            else if (choice == UnsavedChangesChoice.Discard)
            {
                _annotationStore.RemoveTab(tab.Id);
                _history.Clear(tab.Id);
            }
        }

        return true;
    }

    private async Task<bool> SaveAndCloseTabCoreAsync(DocumentTab tab, CancellationToken cancellationToken)
    {
        await _editSaveService.SaveTabAsync(tab, tab.FilePath, cancellationToken);
        return await CloseTabCoreAsync(tab.Id, cancellationToken);
    }

    private async Task<bool> CloseTabCoreAsync(Guid tabId, CancellationToken cancellationToken)
    {
        _annotationStore.RemoveTab(tabId);
        _history.Clear(tabId);
        await _tabService.CloseTabAsync(tabId, cancellationToken);
        return true;
    }
}
