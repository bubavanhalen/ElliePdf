using System.Buffers;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Printing;
using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using PdfPrintPageRange = ElliePdf.Pdf.Contracts.PrintPageRange;

namespace ElliePdf.Pages;

public sealed partial class ReaderPage
{
    private PrintPipeline _printPipeline = null!;
    private IPdfService _printPdfService = null!;
    private IDocumentTabService _printTabs = null!;
    private CancellationTokenSource? _printAvailabilityCancellation;
    private PdfDocumentSession? _permissionDocument;
    private PrintJobContext? _printJob;
    private bool _printingEventsAttached;
    private bool _activeDocumentCanPrint;

    private void InitializePrinting()
    {
        Loaded += OnPrintingLoaded;
        Unloaded += OnPrintingUnloaded;
    }

    private void OnPrintingLoaded(object sender, RoutedEventArgs args)
    {
        if (!_printingEventsAttached)
        {
            ViewModel.PropertyChanged += OnPrintingViewModelPropertyChanged;
            _printTabs.StateChanged += OnPrintingDocumentStateChanged;
            _printingEventsAttached = true;
        }

        ObserveBackground(UpdatePrintAvailabilityAsync(force: true), "reader-print-permissions");
    }

    private void OnPrintingUnloaded(object sender, RoutedEventArgs args)
    {
        if (_printingEventsAttached)
        {
            ViewModel.PropertyChanged -= OnPrintingViewModelPropertyChanged;
            _printTabs.StateChanged -= OnPrintingDocumentStateChanged;
            _printingEventsAttached = false;
        }

        _printAvailabilityCancellation?.Cancel();
        _printAvailabilityCancellation?.Dispose();
        _printAvailabilityCancellation = null;
        CleanupPrintJob(_printJob);
    }

    private void OnPrintingViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ReaderViewModel.HasDocument) or nameof(ReaderViewModel.DocumentTitle))
        {
            ObserveBackground(UpdatePrintAvailabilityAsync(), "reader-print-permissions");
        }
    }

    private void OnPrintingDocumentStateChanged(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(_permissionDocument, _printTabs.ActiveDocument))
        {
            ObserveBackground(UpdatePrintAvailabilityAsync(), "reader-print-permissions");
        }
    }

    private async Task UpdatePrintAvailabilityAsync(bool force = false)
    {
        var document = _printTabs.ActiveDocument;
        if (!force && ReferenceEquals(document, _permissionDocument))
        {
            UpdatePrintCommandState();
            return;
        }

        _permissionDocument = document;
        _activeDocumentCanPrint = false;
        _printAvailabilityCancellation?.Cancel();
        _printAvailabilityCancellation?.Dispose();
        _printAvailabilityCancellation = new CancellationTokenSource();
        var cancellationToken = _printAvailabilityCancellation.Token;

        if (document is null)
        {
            UpdatePrintCommandState();
            return;
        }

        try
        {
            var permissions = await _printPdfService.GetPermissionsAsync(document, cancellationToken);
            if (!cancellationToken.IsCancellationRequested
                && ReferenceEquals(document, _printTabs.ActiveDocument))
            {
                _activeDocumentCanPrint = permissions.CanPrint && document.PageCount > 0;
                UpdatePrintCommandState();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (ReferenceEquals(document, _printTabs.ActiveDocument))
            {
                _activeDocumentCanPrint = false;
                UpdatePrintCommandState(availabilityUnknown: true);
            }
        }
    }

    private void UpdatePrintCommandState(bool availabilityUnknown = false)
    {
        var hasDocument = _printTabs.ActiveDocument is not null;
        PrintCommandButton.IsEnabled = hasDocument && _activeDocumentCanPrint && _printJob is null;
        ToolTipService.SetToolTip(
            PrintCommandButton,
            _printJob is not null
                ? AppResources.Get("Print_BusyTooltip")
                : !hasDocument
                    ? AppResources.Get("Print_OpenDocumentTooltip")
                    : availabilityUnknown
                        ? AppResources.Get("Print_AvailabilityUnknownTooltip")
                        : !_activeDocumentCanPrint
                            ? AppResources.Get("Print_PermissionDeniedTooltip")
                            : AppResources.Get("Print_AllowedTooltip"));
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e) =>
        ObserveBackground(PrintAsync(), "reader-print");

    private async Task PrintAsync()
    {
        if (_printJob is not null)
        {
            return;
        }

        var document = _printTabs.ActiveDocument;
        if (document is null || document.PageCount <= 0)
        {
            return;
        }

        PdfPermissions permissions;
        try
        {
            permissions = await _printPdfService.GetPermissionsAsync(document);
        }
        catch
        {
            await ShowPrintMessageAsync("Print_ErrorTitle", "Print_ErrorMessage");
            return;
        }

        if (!permissions.CanPrint)
        {
            _activeDocumentCanPrint = false;
            UpdatePrintCommandState();
            await ShowPrintMessageAsync("Print_PermissionDeniedTitle", "Print_PermissionDeniedMessage");
            return;
        }

        if (!PrintManager.IsSupported())
        {
            await ShowPrintMessageAsync("Print_UnsupportedTitle", "Print_UnsupportedMessage");
            return;
        }

        var choice = await PromptPrintOptionsAsync(document.PageCount);
        if (choice is null || !ReferenceEquals(document, _printTabs.ActiveDocument))
        {
            return;
        }

        var pageIndices = PrintPageRangeExpander.Expand(
            choice.Selection,
            document.PageCount,
            Math.Clamp(ViewModel.CurrentPageIndex, 0, document.PageCount - 1));
        var printDocument = new PrintDocument();
        var manager = PrintManagerInterop.GetForWindow(_uiHost.WindowHandle);
        var job = new PrintJobContext(
            document,
            choice.Selection,
            choice.Scaling,
            pageIndices,
            ViewModel.CurrentPageIndex,
            printDocument,
            manager);
        _printJob = job;

        printDocument.Paginate += OnPrintPaginate;
        printDocument.GetPreviewPage += OnPrintGetPreviewPage;
        printDocument.AddPages += OnPrintAddPages;
        manager.PrintTaskRequested += OnPrintTaskRequested;
        UpdatePrintCommandState();

        try
        {
            var shown = await PrintManager.ShowPrintUIAsync();
            if (!shown)
            {
                CleanupPrintJob(job);
            }
        }
        catch
        {
            CleanupPrintJob(job);
            await ShowPrintMessageAsync("Print_UnsupportedTitle", "Print_UnsupportedMessage");
        }
    }

    private async Task<PrintDialogChoice?> PromptPrintOptionsAsync(int pageCount)
    {
        var allPagesRadio = new RadioButton
        {
            Content = AppResources.Get("Print_AllPages"),
            IsChecked = true
        };
        var currentPageRadio = new RadioButton
        {
            Content = AppResources.Get("Print_CurrentPage")
        };
        var customRangeRadio = new RadioButton
        {
            Content = AppResources.Get("Print_PageRange")
        };
        var fromBox = new NumberBox
        {
            Header = AppResources.Get("Print_From"),
            Minimum = 1,
            Maximum = pageCount,
            Value = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            IsEnabled = false
        };
        var toBox = new NumberBox
        {
            Header = AppResources.Get("Print_To"),
            Minimum = 1,
            Maximum = pageCount,
            Value = pageCount,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            IsEnabled = false
        };

        void UpdateRangeInputs() =>
            fromBox.IsEnabled = toBox.IsEnabled = customRangeRadio.IsChecked == true;

        allPagesRadio.Checked += (_, _) => UpdateRangeInputs();
        currentPageRadio.Checked += (_, _) => UpdateRangeInputs();
        customRangeRadio.Checked += (_, _) => UpdateRangeInputs();

        var fitRadio = new RadioButton
        {
            Content = AppResources.Get("Print_FitToPrintableArea"),
            IsChecked = true
        };
        var actualSizeRadio = new RadioButton
        {
            Content = AppResources.Get("Print_ActualSize")
        };
        var dialog = new ContentDialog
        {
            Title = AppResources.Get("Print_Title"),
            PrimaryButtonText = AppResources.Get("Print_Action"),
            CloseButtonText = AppResources.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    allPagesRadio,
                    currentPageRadio,
                    customRangeRadio,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { fromBox, toBox }
                    },
                    new TextBlock
                    {
                        Text = AppResources.Get("Print_ScalingHeading"),
                        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"]
                    },
                    fitRadio,
                    actualSizeRadio,
                    new TextBlock
                    {
                        Text = AppResources.Get("Print_AutoOrientationExplanation"),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75
                    }
                }
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        PrintPageSelection selection;
        if (currentPageRadio.IsChecked == true)
        {
            selection = PrintPageSelection.CurrentPage();
        }
        else if (customRangeRadio.IsChecked == true)
        {
            var from = checked((int)Math.Clamp(fromBox.Value, 1, pageCount)) - 1;
            var to = checked((int)Math.Clamp(toBox.Value, from + 1, pageCount)) - 1;
            selection = PrintPageSelection.Custom([new PdfPrintPageRange(from, to)]);
        }
        else
        {
            selection = PrintPageSelection.AllPages();
        }

        return new PrintDialogChoice(
            selection,
            actualSizeRadio.IsChecked == true
                ? PrintScalingMode.ActualSize
                : PrintScalingMode.FitToPrintableArea);
    }

    private async Task ShowPrintMessageAsync(string titleResource, string messageResource)
    {
        var dialog = new ContentDialog
        {
            Title = AppResources.Get(titleResource),
            Content = AppResources.Get(messageResource),
            CloseButtonText = AppResources.Get("Common_Close"),
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var job = _printJob;
        if (job is null || !ReferenceEquals(sender, job.Manager))
        {
            return;
        }

        var deferral = args.Request.GetDeferral();
        try
        {
            var task = args.Request.CreatePrintTask(AppResources.Get("App_Name"), sourceRequested =>
            {
                if (ReferenceEquals(_printJob, job) && !job.IsCancellationRequested)
                {
                    sourceRequested.SetSource(job.DocumentSource.DocumentSource);
                }
            });
            job.PrintTask = task;
            task.Completed += OnPrintTaskCompleted;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_printJob is { } job && ReferenceEquals(sender, job.PrintTask))
            {
                CleanupPrintJob(job);
            }
        });
    }

    private void OnPrintPaginate(object? sender, PaginateEventArgs args)
    {
        var job = _printJob;
        if (job is null || !ReferenceEquals(sender, job.DocumentSource))
        {
            return;
        }

        var pageDescription = args.PrintTaskOptions.GetPageDescription(0);
        job.Configure(args.PrintTaskOptions, CreatePrintTarget(pageDescription));
        job.DocumentSource.SetPreviewPageCount(job.PageIndices.Length, PreviewPageCountType.Final);
    }

    private void OnPrintGetPreviewPage(object? sender, GetPreviewPageEventArgs args)
    {
        var job = _printJob;
        if (job is null || !ReferenceEquals(sender, job.DocumentSource))
        {
            return;
        }

        var operation = RenderPrintPreviewAsync(job, args.PageNumber);
        job.Track(operation);
        ObserveBackground(operation, "reader-print-preview-page");
    }

    private async Task RenderPrintPreviewAsync(PrintJobContext job, int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > job.PageIndices.Length || !job.IsConfigured)
        {
            return;
        }

        var cancellation = job.ReplacePreviewCancellation();
        var cancellationToken = cancellation.Token;
        var pageIndex = job.PageIndices[pageNumber - 1];
        var description = job.Options!.GetPageDescription((uint)(pageNumber - 1));
        var request = new PrintPipelineRequest(
            job.Document.EngineSession.DocumentId,
            PrintPageSelection.Custom([new PdfPrintPageRange(pageIndex, pageIndex)]),
            job.Target!,
            job.Scaling);
        var consumer = new WinUiPrintSurfaceConsumer(
            DispatcherQueue,
            _ => description,
            (_, page) =>
            {
                if (ReferenceEquals(_printJob, job) && !cancellationToken.IsCancellationRequested)
                {
                    job.DocumentSource.SetPreviewPage(pageNumber, page);
                }
            });

        try
        {
            await _printPipeline.ExecuteAsync(
                job.Document.EngineSession,
                request,
                job.CurrentPageIndex,
                consumer,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            job.ReleasePreviewCancellation(cancellation);
        }
    }

    private void OnPrintAddPages(object? sender, AddPagesEventArgs args)
    {
        var job = _printJob;
        if (job is null
            || !ReferenceEquals(sender, job.DocumentSource)
            || !job.TryBeginSpooling()
            || !job.IsConfigured)
        {
            return;
        }

        var operation = AddPrintPagesAsync(job);
        job.Track(operation);
        ObserveBackground(operation, "reader-print-add-pages");
    }

    private async Task AddPrintPagesAsync(PrintJobContext job)
    {
        job.CancelPreview();
        var cancellationToken = job.CancellationToken;
        var request = new PrintPipelineRequest(
            job.Document.EngineSession.DocumentId,
            job.Selection,
            job.Target!,
            job.Scaling);
        var consumer = new WinUiPrintSurfaceConsumer(
            DispatcherQueue,
            ordinal => job.Options!.GetPageDescription((uint)ordinal),
            (_, page) =>
            {
                if (ReferenceEquals(_printJob, job) && !cancellationToken.IsCancellationRequested)
                {
                    job.DocumentSource.AddPage(page);
                }
            });

        try
        {
            await _printPipeline.ExecuteAsync(
                job.Document.EngineSession,
                request,
                job.CurrentPageIndex,
                consumer,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            await DispatcherEnqueueAsync(
                DispatcherQueue,
                () =>
                {
                    if (ReferenceEquals(_printJob, job))
                    {
                        job.DocumentSource.AddPagesComplete();
                    }
                },
                CancellationToken.None);
            return;
        }

        await DispatcherEnqueueAsync(
            DispatcherQueue,
            () =>
            {
                if (ReferenceEquals(_printJob, job) && !cancellationToken.IsCancellationRequested)
                {
                    job.DocumentSource.AddPagesComplete();
                }
            },
            cancellationToken);
    }

    private static PrintTarget CreatePrintTarget(PrintPageDescription description)
    {
        var printable = description.ImageableRect;
        var widthPoints = Math.Max(1, printable.Width * 72d / 96d);
        var heightPoints = Math.Max(1, printable.Height * 72d / 96d);
        var reportedDpi = Math.Min(description.DpiX, description.DpiY);
        var dpi = reportedDpi == 0 ? 150 : Math.Clamp(checked((int)reportedDpi), 96, 300);
        return new PrintTarget(widthPoints, heightPoints, dpi);
    }

    private void CleanupPrintJob(PrintJobContext? job)
    {
        if (job is null || !job.TryClose())
        {
            return;
        }

        job.Manager.PrintTaskRequested -= OnPrintTaskRequested;
        job.DocumentSource.Paginate -= OnPrintPaginate;
        job.DocumentSource.GetPreviewPage -= OnPrintGetPreviewPage;
        job.DocumentSource.AddPages -= OnPrintAddPages;
        if (job.PrintTask is not null)
        {
            job.PrintTask.Completed -= OnPrintTaskCompleted;
        }

        if (ReferenceEquals(_printJob, job))
        {
            _printJob = null;
            UpdatePrintCommandState();
        }

        ObserveBackground(job.DisposeWhenIdleAsync(), "reader-print-cleanup");
    }

    private static async Task DispatcherEnqueueAsync(
        DispatcherQueue dispatcher,
        Action action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!dispatcher.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            throw new InvalidOperationException("The print surface could not be dispatched to the UI thread.");
        }

        await completion.Task;
    }

    private sealed record PrintDialogChoice(PrintPageSelection Selection, PrintScalingMode Scaling);

    private sealed class PrintJobContext
    {
        private readonly object _operationGate = new();
        private readonly List<Task> _operations = [];
        private readonly CancellationTokenSource _cancellation = new();
        private CancellationTokenSource? _previewCancellation;
        private int _closed;
        private int _spooling;

        public PrintJobContext(
            PdfDocumentSession document,
            PrintPageSelection selection,
            PrintScalingMode scaling,
            ImmutableArray<int> pageIndices,
            int currentPageIndex,
            PrintDocument documentSource,
            PrintManager manager)
        {
            Document = document;
            Selection = selection;
            Scaling = scaling;
            PageIndices = pageIndices;
            CurrentPageIndex = currentPageIndex;
            DocumentSource = documentSource;
            Manager = manager;
        }

        public PdfDocumentSession Document { get; }
        public PrintPageSelection Selection { get; }
        public PrintScalingMode Scaling { get; }
        public ImmutableArray<int> PageIndices { get; }
        public int CurrentPageIndex { get; }
        public PrintDocument DocumentSource { get; }
        public PrintManager Manager { get; }
        public PrintTask? PrintTask { get; set; }
        public PrintTaskOptions? Options { get; private set; }
        public PrintTarget? Target { get; private set; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;
        public bool IsConfigured => Options is not null && Target is not null;

        public void Configure(PrintTaskOptions options, PrintTarget target)
        {
            Options = options;
            Target = target;
        }

        public CancellationTokenSource ReplacePreviewCancellation()
        {
            var replacement = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            var previous = Interlocked.Exchange(ref _previewCancellation, replacement);
            previous?.Cancel();
            return replacement;
        }

        public void ReleasePreviewCancellation(CancellationTokenSource cancellation)
        {
            Interlocked.CompareExchange(ref _previewCancellation, null, cancellation);
            cancellation.Dispose();
        }

        public void CancelPreview() => Volatile.Read(ref _previewCancellation)?.Cancel();

        public bool TryBeginSpooling() => Interlocked.CompareExchange(ref _spooling, 1, 0) == 0;

        public void Track(Task operation)
        {
            lock (_operationGate)
            {
                _operations.Add(operation);
            }
        }

        public bool TryClose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return false;
            }

            _cancellation.Cancel();
            CancelPreview();
            return true;
        }

        public async Task DisposeWhenIdleAsync()
        {
            var observedCount = -1;
            while (true)
            {
                Task[] operations;
                lock (_operationGate)
                {
                    operations = [.. _operations];
                }

                try
                {
                    await Task.WhenAll(operations);
                }
                catch
                {
                    // The owning BackgroundTaskSupervisor observes individual failures.
                }

                lock (_operationGate)
                {
                    if (_operations.Count == operations.Length && observedCount == operations.Length)
                    {
                        break;
                    }
                    observedCount = _operations.Count;
                }
            }

            var preview = Interlocked.Exchange(ref _previewCancellation, null);
            preview?.Dispose();
            _cancellation.Dispose();
        }
    }

    private sealed class WinUiPrintSurfaceConsumer : IPrintSurfaceConsumer
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly Func<int, PrintPageDescription> _descriptionForOrdinal;
        private readonly Action<int, Grid> _pageCompleted;
        private PageVisual? _page;
        private int _pageOrdinal;

        public WinUiPrintSurfaceConsumer(
            DispatcherQueue dispatcher,
            Func<int, PrintPageDescription> descriptionForOrdinal,
            Action<int, Grid> pageCompleted)
        {
            _dispatcher = dispatcher;
            _descriptionForOrdinal = descriptionForOrdinal;
            _pageCompleted = pageCompleted;
        }

        public ValueTask ConsumeAsync(PrintPageSurface surface, CancellationToken cancellationToken)
        {
            if (surface.PixelBuffer is not IReadablePixelBufferLease readable)
            {
                throw new InvalidOperationException("The isolated worker returned a non-readable print surface.");
            }

            return new ValueTask(DispatcherEnqueueAsync(
                _dispatcher,
                () => ConsumeOnUiThread(surface, readable, cancellationToken),
                cancellationToken));
        }

        private void ConsumeOnUiThread(
            PrintPageSurface surface,
            IReadablePixelBufferLease readable,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (surface.IsFirstTile)
            {
                _page = CreatePageVisual(
                    surface.Plan,
                    _descriptionForOrdinal(_pageOrdinal));
            }

            var page = _page ?? throw new InvalidOperationException("A print tile arrived without a page surface.");
            var bitmap = new WriteableBitmap(readable.Width, readable.Height);
            using (var input = readable.OpenReadStream())
            using (var output = bitmap.PixelBuffer.AsStream())
            {
                CopyPackedBgra(
                    input,
                    output,
                    readable.Width,
                    readable.Height,
                    readable.Stride,
                    cancellationToken);
            }
            bitmap.Invalidate();

            var tile = surface.PixelBuffer.Key.Tile;
            var leftBleed = tile.X > 0 ? tile.BleedPixels : 0;
            var topBleed = tile.Y > 0 ? tile.BleedPixels : 0;
            var image = new Image
            {
                Source = bitmap,
                Width = readable.Width * page.PixelsToDipsX,
                Height = readable.Height * page.PixelsToDipsY,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            Canvas.SetLeft(image, page.ContentLeft + (tile.X - leftBleed) * page.PixelsToDipsX);
            Canvas.SetTop(image, page.ContentTop + (tile.Y - topBleed) * page.PixelsToDipsY);
            page.Surface.Children.Add(image);

            if (surface.IsLastTile)
            {
                _pageCompleted(_pageOrdinal, page.Root);
                _pageOrdinal++;
                _page = null;
            }
        }

        private static PageVisual CreatePageVisual(
            PrintPagePlan plan,
            PrintPageDescription description)
        {
            var pageWidth = description.PageSize.Width;
            var pageHeight = description.PageSize.Height;
            var imageable = description.ImageableRect;
            var targetLandscape = plan.IsLandscape;
            var descriptionLandscape = pageWidth > pageHeight;
            if (targetLandscape != descriptionLandscape)
            {
                (pageWidth, pageHeight) = (pageHeight, pageWidth);
                imageable = new Windows.Foundation.Rect(
                    Math.Max(0, (pageWidth - imageable.Height) / 2),
                    Math.Max(0, (pageHeight - imageable.Width) / 2),
                    imageable.Height,
                    imageable.Width);
            }

            var root = new Grid
            {
                Width = pageWidth,
                Height = pageHeight,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, pageWidth, pageHeight)
                }
            };
            var surface = new Canvas
            {
                Width = pageWidth,
                Height = pageHeight,
                IsHitTestVisible = false
            };
            root.Children.Add(surface);

            var contentWidth = plan.SizeInPoints.Width * plan.EffectiveScale * 96d / 72d;
            var contentHeight = plan.SizeInPoints.Height * plan.EffectiveScale * 96d / 72d;
            var pixelsToDipsX = contentWidth / plan.RasterWidth;
            var pixelsToDipsY = contentHeight / plan.RasterHeight;
            var contentLeft = imageable.X + (imageable.Width - contentWidth) / 2;
            var contentTop = imageable.Y + (imageable.Height - contentHeight) / 2;
            return new PageVisual(root, surface, contentLeft, contentTop, pixelsToDipsX, pixelsToDipsY);
        }

        private static void CopyPackedBgra(
            Stream input,
            Stream output,
            int width,
            int height,
            int stride,
            CancellationToken cancellationToken)
        {
            var packedStride = checked(width * 4);
            var row = ArrayPool<byte>.Shared.Rent(stride);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    input.ReadExactly(row.AsSpan(0, stride));
                    output.Write(row, 0, packedStride);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(row);
            }
        }

        private sealed record PageVisual(
            Grid Root,
            Canvas Surface,
            double ContentLeft,
            double ContentTop,
            double PixelsToDipsX,
            double PixelsToDipsY);
    }
}
