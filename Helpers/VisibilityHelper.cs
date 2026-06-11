using Microsoft.UI.Xaml;

namespace ElliePdf.Helpers;

public static class VisibilityHelper
{
    public static Visibility FromBoolean(bool isVisible) =>
        isVisible ? Visibility.Visible : Visibility.Collapsed;
}
