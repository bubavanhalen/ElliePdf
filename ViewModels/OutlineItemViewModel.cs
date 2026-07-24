using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed partial class OutlineItemViewModel : ObservableObject
{
    public OutlineItemViewModel(PdfOutlineItem item, int depth)
    {
        Title = item.Title;
        PageIndex = item.PageIndex;
        Depth = depth;
        IndentMargin = new Thickness(depth * 12, 0, 0, 0);
        Children = item.Children
            .Select(child => new OutlineItemViewModel(child, depth + 1))
            .ToList();
    }

    public string Title { get; }

    public int PageIndex { get; }

    public int Depth { get; }

    public Thickness IndentMargin { get; }

    public IReadOnlyList<OutlineItemViewModel> Children { get; }
}
