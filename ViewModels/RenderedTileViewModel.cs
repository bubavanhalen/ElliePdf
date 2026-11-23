using ElliePdf.Domain.Documents;
using Microsoft.UI.Xaml.Media;

namespace ElliePdf.ViewModels;

public sealed record RenderedTileViewModel(
    RenderKey Key,
    ImageSource Image,
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsVisible);
