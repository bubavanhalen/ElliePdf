using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Services;
using System.Collections.ObjectModel;
using ElliePdf.Semantics;

namespace ElliePdf.ViewModels;

public sealed partial class RenderedPageViewModel : ObservableObject
{
    public RenderedPageViewModel(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        float pageWidthPoints,
        float pageHeightPoints)
    {
        PageIndex = pageIndex;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PageWidthPoints = pageWidthPoints;
        PageHeightPoints = pageHeightPoints;
    }

    public int PageIndex { get; }

    public string AutomationName => AppResources.Format("Reader_PageAutomationName", PageIndex + 1);

    public ObservableCollection<RenderedTileViewModel> Tiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale))]
    public partial int PixelWidth { get; set; }

    [ObservableProperty]
    public partial int PixelHeight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale))]
    public partial float PageWidthPoints { get; set; }

    [ObservableProperty]
    public partial float PageHeightPoints { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    public bool HasPixels => Tiles.Count != 0;

    public void ReplaceTiles(IEnumerable<RenderedTileViewModel> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Tiles.Clear();
        foreach (var tile in tiles)
        {
            Tiles.Add(tile);
        }

        OnPropertyChanged(nameof(HasPixels));
    }

    public void ClearTiles()
    {
        Tiles.Clear();
        OnPropertyChanged(nameof(HasPixels));
    }

    [ObservableProperty]
    public partial IReadOnlyList<PdfRect> SearchHighlights { get; set; } = [];

    [ObservableProperty]
    public partial SemanticPageSnapshot? SemanticPage { get; set; }

    [ObservableProperty]
    public partial bool CanCopy { get; set; }

    public double DisplayScale => PageWidthPoints > 0 ? PixelWidth / PageWidthPoints : 1.0;
}
