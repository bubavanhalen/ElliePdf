namespace ElliePdf.Services;

/// <summary>Explicit, Debug-only review surface. Never changes the release reader by default.</summary>
internal static class ReaderDesignPreview
{
    public static bool IsEnabled { get; private set; }

    public static void Configure(string? arguments)
    {
#if DEBUG
        IsEnabled = string.Equals(arguments?.Trim(), "--design-preview", StringComparison.Ordinal);
#endif
    }
}
