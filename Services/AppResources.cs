using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ElliePdf.Services;

internal static class AppResources
{
    private static readonly Lazy<ResourceLoader> Loader = new(static () => new ResourceLoader());

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = Loader.Value.GetString(key);
        return string.IsNullOrEmpty(value) ? $"[{key}]" : value;
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static string FormatPlural(
        string singularKey,
        string pluralKey,
        long count,
        params object?[] arguments) =>
        Format(count == 1 ? singularKey : pluralKey, arguments);
}
