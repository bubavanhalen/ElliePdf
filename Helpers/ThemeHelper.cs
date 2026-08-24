using Microsoft.UI.Xaml;

namespace ElliePdf.Helpers;

public static class ThemeHelper
{
    public static ElementTheme Parse(string? theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    public static void Apply(string? theme)
    {
        if (App.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Parse(theme);
        }
    }
}
