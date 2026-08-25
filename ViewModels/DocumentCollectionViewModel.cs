using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Helpers;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.ViewModels;

public sealed class DocumentCollectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPdfService _pdfService;
    private readonly IDocumentOpenService _documentOpenService;
    private readonly IDocumentTabService _tabService;
    private readonly IUserSettingsService _settingsService;
    private readonly IInPlaceSaveService _inPlaceSaveService;
    private readonly List<PdfDocumentSession> _sourceDocuments = [];
    private ObservableCollection<DocumentItemViewModel> _pages = [];
    private bool _isBusy;
    private bool _isStatusOpen;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private int _statusVersion;

    public DocumentCollectionViewModel(
        IPdfService pdfService,
        IDocumentOpenService documentOpenService,
        IDocumentTabService tabService,
        IUserSettingsService settingsService,
        IInPlaceSaveService inPlaceSaveService)
    {
        _pdfService = pdfService;
        _documentOpenService = documentOpenService;
        _tabService = tabService;
        _settingsService = settingsService;
        _inPlaceSaveService = inPlaceSaveService;
        _inPlaceSaveService.SessionReplaced += (_, args) => RemapSession(args.OldSession, args.NewSession);
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

    public event EventHandler<string>? MergeCompleted;

    public async Task ImportDocumentsAsync(
        IReadOnlyList<string> filePaths,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        if (!append)
        {
            await ClearDocumentsAsync(cancellationToken);
        }

        IsBusy = true;
        DismissStatus();

        try
        {
            foreach (var filePath in filePaths)
            {
                await AddDocumentAsync(filePath, cancellationToken);
            }
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
            // User cancelled the import; no status needed.
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
        Navigation.AppNavigation.RequestWorkspace("read");
        Navigation.AppNavigation.RequestReaderAtPage(item.PageIndex);
    }

    public async Task RotatePageAsync(DocumentItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (item is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _pdfService.RotatePageAsync(item.Document, item.PageIndex, 1, cancellationToken);
            var thumbnailBytes = await _pdfService.RenderPageThumbnailAsync(item.Document, item.PageIndex, 240, 320, cancellationToken);
            item.Thumbnail = await BitmapHelper.CreateBitmapAsync(thumbnailBytes);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeletePageAsync(DocumentItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (item is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _pdfService.DeletePageAsync(item.Document, item.PageIndex, cancellationToken);

            var itemsInDocument = Pages
                .Where(page => ReferenceEquals(page.Document, item.Document))
                .OrderBy(page => page.PageIndex)
                .ToList();

            Pages.Remove(item);

            foreach (var remainingPage in itemsInDocument.Where(page => page.PageIndex > item.PageIndex))
            {
                remainingPage.PageIndex -= 1;
                remainingPage.DisplayName = $"Page {remainingPage.PageIndex + 1}";
            }

            if (item.Document.PageCount == 0)
            {
                _sourceDocuments.Remove(item.Document);
                await item.Document.DisposeAsync();
            }
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (_sourceDocuments.Count == 0)
        {
            SetStatus("Import a PDF before saving.", InfoBarSeverity.Informational);
            return;
        }

        if (_settingsService.Settings.ConfirmOrganizeSave)
        {
            var confirmed = await ConfirmOrganizeSaveAsync(cancellationToken);
            if (!confirmed)
            {
                return;
            }
        }

        IsBusy = true;

        try
        {
            var saved = 0;
            var failures = new List<string>();

            foreach (var document in _sourceDocuments.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = document.SourcePath;
                var result = await _inPlaceSaveService.SaveInPlaceAsync(document, null, cancellationToken);

                if (result.Saved)
                {
                    saved++;
                }
                else
                {
                    failures.Add($"{Path.GetFileName(sourcePath)}: {result.ErrorMessage}");
                }
            }

            if (failures.Count > 0)
            {
                SetStatus($"Could not save {string.Join("; ", failures)}", InfoBarSeverity.Error);
            }
            else
            {
                SetStatus($"Saved {saved} document(s).", InfoBarSeverity.Success);
            }
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Swaps every reference to a replaced session after an in-place save.</summary>
    private void RemapSession(PdfDocumentSession oldSession, PdfDocumentSession newSession)
    {
        if (ReferenceEquals(oldSession, newSession))
        {
            return;
        }

        for (var index = 0; index < _sourceDocuments.Count; index++)
        {
            if (ReferenceEquals(_sourceDocuments[index], oldSession))
            {
                _sourceDocuments[index] = newSession;
            }
        }

        foreach (var page in Pages.Where(page => ReferenceEquals(page.Document, oldSession)))
        {
            page.Document = newSession;
        }
    }

    public async Task<string?> MergeDocumentsAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (Pages.Count < 1)
        {
            SetStatus("Import at least one page before merging.", InfoBarSeverity.Informational);
            return null;
        }

        IsBusy = true;

        try
        {
            var orderedPages = Pages
                .Select(page => (page.Document, page.PageIndex))
                .ToList();

            if (orderedPages.Count == 1 && _sourceDocuments.Count == 1)
            {
                await _pdfService.SaveDocumentAsync(_sourceDocuments[0], outputPath, cancellationToken);
            }
            else
            {
                await _pdfService.MergeOrderedPagesAsync(orderedPages, outputPath, cancellationToken);
            }

            // The export-complete dialog confirms the result; no toast needed.
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
        finally
        {
            IsBusy = false;
        }
    }

    public void DismissStatus() => IsStatusOpen = false;

    public async ValueTask DisposeAsync() => await ClearDocumentsAsync();

    private async Task AddDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        var document = await _documentOpenService.OpenAsync(filePath, cancellationToken);
        _sourceDocuments.Add(document);

        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            var thumbnailBytes = await _pdfService.RenderPageThumbnailAsync(document, pageIndex, 240, 320, cancellationToken);
            var item = new DocumentItemViewModel(document, filePath, pageIndex)
            {
                Thumbnail = await BitmapHelper.CreateBitmapAsync(thumbnailBytes)
            };

            Pages.Add(item);
        }
    }

    private async Task ClearDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var tabSessions = _tabService.Tabs.Select(tab => tab.Session).ToHashSet();
        foreach (var sourceDocument in _sourceDocuments.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tabSessions.Contains(sourceDocument))
            {
                await sourceDocument.DisposeAsync();
            }
        }

        _sourceDocuments.Clear();
        Pages.Clear();
        DismissStatus();
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

    private static async Task<bool> ConfirmOrganizeSaveAsync(CancellationToken cancellationToken)
    {
        var xamlRoot = GetXamlRoot();
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "Save changes to original files?",
            Content = "This will overwrite the original PDF files with your organize changes. This cannot be undone.",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        cancellationToken.ThrowIfCancellationRequested();
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static Microsoft.UI.Xaml.XamlRoot? GetXamlRoot() =>
        App.Window.Content is Microsoft.UI.Xaml.FrameworkElement root ? root.XamlRoot : null;
}
