using System.Collections.Specialized;
using ElliePdf.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace ElliePdf.Controls;

/// <summary>Small retained visual surface containing only viewport tiles, never a page bitmap.</summary>
public sealed partial class PdfTileSurface : Canvas
{
    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(
        nameof(Tiles),
        typeof(object),
        typeof(PdfTileSurface),
        new PropertyMetadata(null, OnTilesChanged));

    private INotifyCollectionChanged? _observedCollection;

    public IEnumerable<RenderedTileViewModel>? Tiles
    {
        get => GetValue(TilesProperty) as IEnumerable<RenderedTileViewModel>;
        set => SetValue(TilesProperty, value);
    }

    private static void OnTilesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((PdfTileSurface)dependencyObject).ObserveTiles(args.NewValue);
    }

    private void ObserveTiles(object? value)
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnCollectionChanged;
        }

        _observedCollection = value as INotifyCollectionChanged;
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged += OnCollectionChanged;
        }

        RebuildChildren();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildChildren();

    private void RebuildChildren()
    {
        Children.Clear();
        if (Tiles is null)
        {
            return;
        }

        foreach (var tile in Tiles)
        {
            var image = new Image
            {
                Source = tile.Image,
                Width = Math.Max(0.01, tile.Width),
                Height = Math.Max(0.01, tile.Height),
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            SetLeft(image, tile.Left);
            SetTop(image, tile.Top);
            Children.Add(image);
        }
    }
}
