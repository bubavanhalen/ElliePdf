using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ElliePdf.ViewModels;

public sealed class DocumentCollectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPdfService _pdfService;
    private readonly List<PdfDocumentSession> _sourceDocuments = [];
    private ObservableCollection<DocumentItemViewModel> _pages = [];
    private bool _isBusy;
    private bool _isStatusOpen;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public DocumentCollectionViewModel(IPdfService pdfService)
    {
        _pdfService = pdfService;
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

    public async Task ImportDocumentsAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        await ClearDocumentsAsync(cancellationToken);
        IsBusy = true;
        DismissStatus();

        try
        {
            foreach (var filePath in filePaths)
            {
                var document = await _pdfService.OpenDocumentAsync(filePath, cancellationToken);
                _sourceDocuments.Add(document);

                for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
                {
                    var thumbnailBytes = await _pdfService.RenderPageThumbnailAsync(document, pageIndex, 240, 320, cancellationToken);
                    var item = new DocumentItemViewModel(document, filePath, pageIndex)
                    {
                        Thumbnail = await CreateBitmapAsync(thumbnailBytes)
                    };

                    Pages.Add(item);
                }
            }

            SetStatus(
                $"Loaded {Pages.Count} page(s) from {SourceDocumentCount} document(s).",
                InfoBarSeverity.Success);
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
            item.Thumbnail = await CreateBitmapAsync(thumbnailBytes);
            SetStatus($"Rotated {item.DisplayName}.", InfoBarSeverity.Success);
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
            var deletedDisplayName = item.DisplayName;
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

            SetStatus($"Deleted {deletedDisplayName}.", InfoBarSeverity.Warning);
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

    public async Task MergeDocumentsAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (_sourceDocuments.Count < 2)
        {
            SetStatus("Import at least two PDFs before merging.", InfoBarSeverity.Informational);
            return;
        }

        IsBusy = true;

        try
        {
            await _pdfService.MergeDocumentsAsync(_sourceDocuments, outputPath, cancellationToken);
            SetStatus($"Merged {_sourceDocuments.Count} documents into '{Path.GetFileName(outputPath)}'.", InfoBarSeverity.Success);
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

    public void DismissStatus() => IsStatusOpen = false;

    public async ValueTask DisposeAsync() => await ClearDocumentsAsync();

    private async Task ClearDocumentsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var sourceDocument in _sourceDocuments.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sourceDocument.DisposeAsync();
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
    }

    private static async Task<BitmapImage> CreateBitmapAsync(byte[] imageBytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageBytes.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
