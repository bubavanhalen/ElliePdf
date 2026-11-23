using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.Pages;

public sealed partial class ReaderPage
{
    private CommandBar? _previewNavigationBar;
    private TextBlock? _previewPageLabel;

    // Reuse the real commands, PDF surfaces and dialogs. This is an opt-in native
    // review layout, not a screenshot facade or a second document implementation.
    private void InitializeDesignPreviewLayout()
    {
        if (!ReaderDesignPreview.IsEnabled) return;

        RootGrid.RowDefinitions.Clear();
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var child in RootGrid.Children.OfType<FrameworkElement>()) Grid.SetRow(child, 2);
        Grid.SetRowSpan(InkPalettePopup, 1);
        Grid.SetRow(DocumentTabs, 0);
        Grid.SetRow(ReaderCommandBar, 1);
        Grid.SetRow(EditCommandBar, 1);
        DocumentTabs.Margin = new Thickness(12, 0, 12, 0);

        var status = ReaderStatusArea;
        Grid.SetRow(status, 3);
        status.Margin = new Thickness(12, 0, 12, 0);
        status.HorizontalAlignment = HorizontalAlignment.Stretch;

        _previewNavigationBar = new CommandBar
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            IsDynamicOverflowEnabled = true,
            Margin = new Thickness(12, 0, 12, 0)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_previewNavigationBar, AppResources.Get("Preview_Navigation"));
        Grid.SetRow(_previewNavigationBar, 4);
        RootGrid.Children.Add(_previewNavigationBar);

        // Conventional page and zoom controls get a stable bottom row, never an overlay.
        foreach (var command in ReaderCommandBar.PrimaryCommands.ToArray())
        {
            if (ReferenceEquals(command, OpenCommandButton) || ReferenceEquals(command, SearchCommandButton)) continue;
            ReaderCommandBar.PrimaryCommands.Remove(command);
            _previewNavigationBar.PrimaryCommands.Add(command);
        }
        PageNumberBox.Width = 64;
        _previewPageLabel = new TextBlock { Text = ViewModel.PageLabel, Margin = new Thickness(8, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        _previewNavigationBar.Content = _previewPageLabel;

        // Promote existing actions instead of creating competing command handlers.
        var pages = ReaderCommandBar.SecondaryCommands[1];
        var outline = ReaderCommandBar.SecondaryCommands[2];
        ReaderCommandBar.SecondaryCommands.Remove(pages);
        ReaderCommandBar.SecondaryCommands.Remove(outline);
        ReaderCommandBar.PrimaryCommands.Insert(1, pages);
        ReaderCommandBar.PrimaryCommands.Insert(2, outline);
        ReaderCommandBar.SecondaryCommands.Remove(PrintCommandButton);
        ReaderCommandBar.PrimaryCommands.Add(new AppBarSeparator());
        ReaderCommandBar.PrimaryCommands.Add(new AppBarButton
        {
            Label = AppResources.Get("Reader_Save/Label"), Icon = new SymbolIcon(Symbol.Save), Command = ViewModel.SaveCommand
        });
        ReaderCommandBar.PrimaryCommands.Add(new AppBarButton
        {
            Label = AppResources.Get("Reader_SaveAs/Label"), Icon = new SymbolIcon(Symbol.SaveLocal), Command = ViewModel.SaveAsCommand
        });
        ReaderCommandBar.PrimaryCommands.Add(PrintCommandButton);
        ReaderCommandBar.PrimaryCommands.Add(new AppBarButton
        {
            Label = AppResources.Get("Reader_FitWidth/Label"), Icon = new SymbolIcon(Symbol.FullScreen), Command = ViewModel.ZoomFitWidthCommand
        });
        ReaderCommandBar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
        ReaderCommandBar.Margin = new Thickness(12, 4, 12, 8);
        ReaderCommandBar.VerticalAlignment = VerticalAlignment.Stretch;
        EditCommandBar.Margin = ReaderCommandBar.Margin;
        EditCommandBar.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void ApplyDesignPreviewLayout()
    {
        if (_previewNavigationBar is null) return;
        DocumentTabs.Visibility = !_isFocusMode && ViewModel.TabCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        _previewNavigationBar.Visibility = !_isFocusMode && ViewModel.HasDocument ? Visibility.Visible : Visibility.Collapsed;
        ReaderCommandBar.MaxWidth = double.PositiveInfinity;
        EditCommandBar.MaxWidth = double.PositiveInfinity;
        ReaderSidebar.Margin = new Thickness(0);
        PageViewer.ContentBottomInset = 0;
        ContinuousPagesItems.Margin = new Thickness(0, 16, 0, 16);
        var left = 24d;
        var right = 24d;
        if (ViewModel.IsSidebarOpen && _shellLayout.ReservesDocumentSpace)
        {
            if (ReaderSidebar.HorizontalAlignment == HorizontalAlignment.Right) right += _shellLayout.SidebarWidth;
            else left += _shellLayout.SidebarWidth;
        }
        PageViewer.Margin = new Thickness(left, 0, right, 0);
        ContinuousScrollViewer.Margin = PageViewer.Margin;
    }
}
