using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Domain.Documents;
using ElliePdf.Helpers;
using ElliePdf.Navigation;
using ElliePdf.Pdf.Client;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.ViewModels;

public sealed class DocumentCollectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPdfService _pdfService;
    private readonly IDocumentOpenService _documentOpenService;
    private readonly IDocumentTabService _tabService;
    private readonly IUserSettingsService _settingsService;
    private readonly AppNavigation _navigation;
    private readonly List<PdfDocumentSession> _sourceDocuments = [];
    private readonly Dictionary<PageId, DocumentItemViewModel> _itemsById = [];
    private ObservableCollection<DocumentItemViewModel> _pages = [];
    private bool _isBusy;
    private bool _isStatusOpen;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private OrganizerPagePlan _plan = OrganizerPagePlan.Empty;

    public DocumentCollectionViewModel(
        IPdfService pdfService,
        IDocumentOpenService documentOpenService,
        IDocumentTabService tabService,
        IUserSettingsService settingsService,
        AppNavigation navigation)
    {
        _pdfService = pdfService;
        _documentOpenService = documentOpenService;
        _tabService = tabService;
        _settingsService = settingsService;
        _navigation = navigation;
    }

    public ObservableCollection<DocumentItemViewModel> Pages
    {
        get => _pages;
        private set => SetProperty(ref _pages, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        private set => SetProperty(ref _isStatusOpen, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public int SourceDocumentCount => _sourceDocuments.Count;

    public long PlanRevision => _plan.Revision;

    public OrganizerPagePlan Plan => _plan;

    public bool CanUndo => _plan.CanUndo;

    public bool CanRedo => _plan.CanRedo;

    public bool HasPendingChanges => _plan.IsDirty;

    public event EventHandler<string>? MergeCompleted;

    public async Task ImportDocumentsAsync(
        IReadOnlyList<string> filePaths,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureLabsEnabled())
        {
            return;
        }

        if (filePaths.Count == 0)
        {
            return;
        }

        IsBusy = true;
        DismissStatus();
        var stagedDocuments = new List<PdfDocumentSession>();
        var stagedItems = new List<DocumentItemViewModel>();
        var stagedPages = new List<OrganizerPage>();

        try
        {
            foreach (var filePath in filePaths)
            {
                await AddDocumentAsync(
                    filePath,
                    stagedDocuments,
                    stagedItems,
                    stagedPages,
                    cancellationToken);
            }

            if (!append)
            {
                // All input work succeeded; commit the preview swap as one
                // in-memory state transition and do not let cancellation
                // leave a half-cleared working set.
                await ClearDocumentsAsync(CancellationToken.None);
            }

            _sourceDocuments.AddRange(stagedDocuments);
            stagedDocuments.Clear();
            _plan = append
                ? _plan.Insert(stagedPages, _plan.Pages.Length)
                : OrganizerPagePlan.Create(stagedPages);
            PublishPlan();
            foreach (var item in stagedItems)
            {
                _itemsById[item.PageId] = item;
                Pages.Add(item);
            }

            SetStatus(
                append
                    ? AppResources.Format("Organize_StatusAdded", filePaths.Count, Pages.Count)
                    : AppResources.Format("Organize_StatusLoaded", Pages.Count, SourceDocumentCount),
                InfoBarSeverity.Success);
        }
        catch (PdfiumDependencyException ex)
        {
            await DisposeStagedDocumentsAsync(stagedDocuments);
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            await DisposeStagedDocumentsAsync(stagedDocuments);
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            await DisposeStagedDocumentsAsync(stagedDocuments);
            SetStatus(AppResources.Get("Organize_StatusImportCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is PdfResourceLimitException or PdfWorkerUnavailableException)
        {
            await DisposeStagedDocumentsAsync(stagedDocuments);
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenInReaderAsync(DocumentItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _tabService.LoadDocumentSessionAsync(item.Document, cancellationToken);
        _tabService.CurrentPageIndex = item.PageIndex;
        _navigation.RequestWorkspace("read");
        _navigation.RequestReaderAtPage(item.PageIndex);
    }

    /// <summary>Stages and inserts documents at a plan boundary.</summary>
    public async Task InsertDocumentsAsync(
        IReadOnlyList<string> filePaths,
        int index,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureLabsEnabled() || filePaths.Count == 0)
            return;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Pages.Count);

        IsBusy = true;
        var stagedDocuments = new List<PdfDocumentSession>();
        var stagedItems = new List<DocumentItemViewModel>();
        var stagedPages = new List<OrganizerPage>();
        try
        {
            foreach (var filePath in filePaths)
            {
                await AddDocumentAsync(filePath, stagedDocuments, stagedItems, stagedPages, cancellationToken);
            }

            _sourceDocuments.AddRange(stagedDocuments);
            stagedDocuments.Clear();
            _plan = _plan.Insert(stagedPages, index);
            for (var offset = 0; offset < stagedItems.Count; offset++)
            {
                _itemsById[stagedItems[offset].PageId] = stagedItems[offset];
                Pages.Insert(index + offset, stagedItems[offset]);
            }
            RefreshPageLabels();
            PublishPlan();
            SetStatus(AppResources.Format("Organize_StatusAdded", filePaths.Count, Pages.Count), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            await DisposeStagedDocumentsAsync(stagedDocuments);
            SetStatus(AppResources.Get("Organize_StatusImportCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            await DisposeStagedDocumentsAsync(stagedDocuments);
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RotatePageAsync(DocumentItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (!EnsureLabsEnabled())
        {
            return;
        }

        if (item is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _plan = _plan.Rotate(item.PageId);
            item.Rotation = _plan.Pages.First(page => page.PageId == item.PageId).Rotation;
            PublishPlan();
            SetStatus(AppResources.Format("Organize_StatusRotated", item.DisplayName), InfoBarSeverity.Success);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            SetStatus(AppResources.Get("Organize_StatusImportCancelled"), InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeletePageAsync(DocumentItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (!EnsureLabsEnabled())
        {
            return;
        }

        if (item is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var deletedDisplayName = item.DisplayName;
            cancellationToken.ThrowIfCancellationRequested();
            _plan = _plan.Delete(item.PageId);
            Pages.Remove(item);
            RefreshPageLabels();
            PublishPlan();
            SetStatus(AppResources.Format("Organize_StatusDeleted", deletedDisplayName), InfoBarSeverity.Warning);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            SetStatus(AppResources.Get("Organize_StatusImportCancelled"), InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (!EnsureLabsEnabled())
        {
            return;
        }
        SetStatus(
            AppResources.Get("Organize_StatusSaveAsRequired"),
            InfoBarSeverity.Informational);
        await Task.CompletedTask;
    }

    /// <summary>Commits the current plan to a newly selected destination.</summary>
    public Task SaveDocumentsAsAsync(string outputPath, CancellationToken cancellationToken = default) =>
        MergeDocumentsAsync(outputPath, cancellationToken);

    /// <summary>
    /// Explicit advanced export that may replace one existing destination. The
    /// destination version is captured and revalidated by the atomic store, so
    /// a file changed after confirmation is reported as a conflict.
    /// </summary>
    public Task<string?> OverwriteDocumentsAsync(
        string outputPath,
        CancellationToken cancellationToken = default) =>
        MergeDocumentsCoreAsync(outputPath, overwriteExisting: true, cancellationToken);

    public async Task<string?> MergeDocumentsAsync(string outputPath, CancellationToken cancellationToken = default)
        => await MergeDocumentsCoreAsync(outputPath, overwriteExisting: false, cancellationToken);

    private async Task<string?> MergeDocumentsCoreAsync(
        string outputPath,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        if (!EnsureLabsEnabled())
        {
            return null;
        }

        if (_plan.Pages.Length < 1)
        {
            SetStatus(AppResources.Get("Organize_StatusImportBeforeMerge"), InfoBarSeverity.Informational);
            return null;
        }

        IsBusy = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemsById = Pages.ToDictionary(item => item.PageId);
            var orderedPages = _plan.Pages
                .Select(page =>
                {
                    if (!itemsById.TryGetValue(page.PageId, out var item))
                        throw new InvalidOperationException("The Organizer preview is out of sync.");

                    return new PdfExportPage(
                        item.Document,
                        page.SourcePageIndex,
                        page.SourcePageId,
                        page.SourceContentRevision,
                        page.SourceStructureRevision,
                        page.SourcePageContentRevision,
                        page.Rotation);
                })
                .ToArray();

            await _pdfService.MergeOrderedPagesAsync(
                orderedPages,
                outputPath,
                cancellationToken,
                overwriteExisting);

            SetStatus(AppResources.Format("Organize_StatusExported", orderedPages.Length, Path.GetFileName(outputPath)), InfoBarSeverity.Success);
            MergeCompleted?.Invoke(this, outputPath);
            return outputPath;
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
            return null;
        }
        catch (OperationCanceledException)
        {
            SetStatus(AppResources.Get("Organize_StatusExportCancelled"), InfoBarSeverity.Informational);
            return null;
        }
        finally
        {
            // Detach them again so the reader keeps editing them rather than seeing them twice.
            await DetachOverlaysAfterExportAsync(cancellationToken);
            IsBusy = false;
        }
    }

    /// <summary>Temporarily writes each tab's annotations back into its shared session.</summary>
    private async Task RestoreOverlaysForExportAsync(CancellationToken cancellationToken)
    {
        foreach (var document in _sourceDocuments.ToArray())
        {
            if (OverlaysFor(document) is { } overlays)
            {
                await _pdfService.ApplyOverlaysAsync(document, overlays, cancellationToken);
            }
        }
    }

    private async Task DetachOverlaysAfterExportAsync(CancellationToken cancellationToken)
    {
        foreach (var document in _sourceDocuments.ToArray())
        {
            if (document.IsClosed || OverlaysFor(document) is null)
            {
                continue;
            }

            await _pdfService.ExtractOverlaysAsync(document, cancellationToken);
        }
    }

    public void DismissStatus() => IsStatusOpen = false;

    /// <summary>Discards the in-memory plan without writing any PDF.</summary>
    public Task CancelAsync() => ClearDocumentsAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await ClearDocumentsAsync();

    private async Task AddDocumentAsync(
        string filePath,
        ICollection<PdfDocumentSession> stagedDocuments,
        ICollection<DocumentItemViewModel> stagedItems,
        ICollection<OrganizerPage> stagedPages,
        CancellationToken cancellationToken)
    {
        var document = await _documentOpenService.OpenAsync(filePath, cancellationToken);
        stagedDocuments.Add(document);
        try
        {
            if (document.EngineSession is not IPdfPageMutationSession mutable)
                throw new NotSupportedException("This PDF session cannot provide stable Organizer page identities.");

            var snapshot = mutable.Snapshot;
            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await document.EngineSession
                    .GetPageMetadataAsync(pageIndex, cancellationToken);
                var thumbnailBytes = await _pdfService
                    .RenderPageThumbnailAsync(document, pageIndex, 240, 320, cancellationToken);
                var item = new DocumentItemViewModel(
                    document,
                    filePath,
                    pageIndex,
                    metadata.Id,
                    metadata.Geometry.Rotation)
                {
                    Thumbnail = await BitmapHelper.CreateBitmapAsync(thumbnailBytes)
                };

                stagedItems.Add(item);
                stagedPages.Add(new OrganizerPage(
                    snapshot.Id,
                    metadata.Id,
                    filePath,
                    pageIndex,
                    metadata.Geometry.Rotation,
                    metadata.Label,
                    snapshot.ContentRevision,
                    snapshot.StructureRevision,
                    metadata.ContentRevision));
            }
        }
        catch
        {
            stagedDocuments.Remove(document);
            await document.DisposeAsync();
            throw;
        }
    }

    private async Task ClearDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var tabSessions = _tabService.Tabs
            .Select(static tab => tab.OpenSession)
            .OfType<PdfDocumentSession>()
            .ToHashSet();
        foreach (var sourceDocument in _sourceDocuments.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tabSessions.Contains(sourceDocument))
            {
                await sourceDocument.DisposeAsync();
            }
        }

        _sourceDocuments.Clear();
        _itemsById.Clear();
        Pages.Clear();
        _plan = OrganizerPagePlan.Empty;
        PublishPlan();
        DismissStatus();
    }

    private async Task DisposeStagedDocumentsAsync(IEnumerable<PdfDocumentSession> documents)
    {
        foreach (var document in documents.ToArray())
        {
            await document.DisposeAsync();
        }
    }

    private void PublishPlan()
    {
        OnPropertyChanged(nameof(PlanRevision));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private void RefreshPageLabels()
    {
        for (var index = 0; index < Pages.Count; index++)
        {
            Pages[index].PageIndex = _plan.Pages[index].SourcePageIndex;
            Pages[index].DisplayName = AppResources.Format("Organize_PageName", index + 1);
        }
    }

    public void ReorderPage(DocumentItemViewModel? item, int targetIndex)
    {
        if (!EnsureLabsEnabled() || item is null)
            return;

        try
        {
            _plan = _plan.Reorder(item.PageId, targetIndex);
            var ordered = _plan.Pages
                .Select(page => Pages.First(candidate => candidate.PageId == page.PageId))
                .ToArray();
            Pages.Clear();
            foreach (var page in ordered)
                Pages.Add(page);
            RefreshPageLabels();
            PublishPlan();
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    public void DuplicatePage(DocumentItemViewModel? item)
    {
        if (!EnsureLabsEnabled() || item is null)
            return;

        try
        {
            var source = _plan.Pages.First(page => page.PageId == item.PageId);
            _plan = _plan.Duplicate(item.PageId);
            var duplicatePage = _plan.Pages.First(page =>
                page.PageId != source.PageId
                && page.DocumentId == source.DocumentId
                && page.SourcePageIndex == source.SourcePageIndex
                && !Pages.Any(candidate => candidate.PageId == page.PageId));
            var duplicate = new DocumentItemViewModel(
                item.Document,
                item.FilePath,
                item.PageIndex,
                duplicatePage.PageId,
                duplicatePage.Rotation)
            {
                Thumbnail = item.Thumbnail,
                SourceLabel = item.SourceLabel
            };
            _itemsById[duplicate.PageId] = duplicate;
            var position = Pages.IndexOf(item) + 1;
            Pages.Insert(position, duplicate);
            RefreshPageLabels();
            PublishPlan();
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    public void Undo() => RestoreHistory(redo: false);

    public void Redo() => RestoreHistory(redo: true);

    /// <summary>Returns two immutable previews at a page boundary without changing this plan.</summary>
    public (OrganizerPagePlan Before, OrganizerPagePlan After) SplitPlan(int index) => _plan.SplitAt(index);

    private void RestoreHistory(bool redo)
    {
        if (!EnsureLabsEnabled())
            return;
        var next = redo ? _plan.Redo() : _plan.Undo();
        if (ReferenceEquals(next, _plan))
            return;
        _plan = next;
        Pages.Clear();
        foreach (var page in _plan.Pages)
        {
            if (!_itemsById.TryGetValue(page.PageId, out var item))
                continue;
            item.Rotation = page.Rotation;
            Pages.Add(item);
        }
        RefreshPageLabels();
        PublishPlan();
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;

        // Errors stay until dismissed; everything else fades out on its own.
        var version = ++_statusVersion;
        if (severity != InfoBarSeverity.Error)
        {
            _ = AutoDismissStatusAsync(version);
        }
    }

    private async Task AutoDismissStatusAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (version == _statusVersion)
        {
            IsStatusOpen = false;
        }
    }

    private bool EnsureLabsEnabled()
    {
        if (_settingsService.Settings.EnableLabs)
        {
            return true;
        }

        SetStatus(
            AppResources.Get("Organize_StatusLabsDisabled"),
            InfoBarSeverity.Informational);
        return false;
    }

}
