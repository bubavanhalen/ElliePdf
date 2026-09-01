using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Domain.Documents;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed class DocumentItemViewModel : ObservableObject
{
    private string _filePath = string.Empty;
    private int _pageIndex;
    private BitmapImage? _thumbnail;
    private string _displayName = string.Empty;
    private string _sourceLabel = string.Empty;
    private PageRotation _rotation;

    public DocumentItemViewModel(
        PdfDocumentSession document,
        string filePath,
        int pageIndex,
        PageId? pageId = null,
        PageRotation rotation = PageRotation.None)
    {
        Document = document;
        FilePath = filePath;
        PageIndex = pageIndex;
        PageId = pageId ?? PageId.New();
        Rotation = rotation;
        SourceLabel = Path.GetFileName(filePath);
        DisplayName = AppResources.Format("Organize_PageName", pageIndex + 1);
    }

    public PdfDocumentSession Document { get; internal set; }

    public PageId PageId { get; }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public int PageIndex
    {
        get => _pageIndex;
        set => SetProperty(ref _pageIndex, value);
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string SourceLabel
    {
        get => _sourceLabel;
        set => SetProperty(ref _sourceLabel, value);
    }

    public PageRotation Rotation
    {
        get => _rotation;
        set
        {
            if (!SetProperty(ref _rotation, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RotationAngle));
        }
    }

    public double RotationAngle => (int)Rotation * 90d;
}
