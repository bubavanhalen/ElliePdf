using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed partial class PageThumbnailViewModel : ObservableObject
{
    public PageThumbnailViewModel(int pageIndex, bool isSelected)
    {
        PageIndex = pageIndex;
        Label = $"Page {pageIndex + 1}";
        IsSelected = isSelected;
    }

    public int PageIndex { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
