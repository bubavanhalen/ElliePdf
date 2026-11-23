using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed partial class PageThumbnailViewModel : ObservableObject
{
    public PageThumbnailViewModel(int pageIndex, bool isSelected)
    {
        PageIndex = pageIndex;
        Label = AppResources.Format("Reader_PageAutomationName", pageIndex + 1);
        IsSelected = isSelected;
    }

    public int PageIndex { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionGlyph))]
    [NotifyPropertyChangedFor(nameof(SelectionStatus))]
    public partial bool IsSelected { get; set; }

    public string SelectionGlyph => IsSelected ? "▶" : string.Empty;

    public string SelectionStatus => AppResources.Get(IsSelected ? "Reader_Selected" : "Reader_NotSelected");
}
