using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed partial class PageThumbnailViewModel : ObservableObject
{
    public PageThumbnailViewModel(int pageIndex, BitmapImage thumbnail, bool isSelected)
    {
        PageIndex = pageIndex;
        Thumbnail = thumbnail;
        Label = $"Page {pageIndex + 1}";
        IsSelected = isSelected;
    }

    public int PageIndex { get; }

    public BitmapImage Thumbnail { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
