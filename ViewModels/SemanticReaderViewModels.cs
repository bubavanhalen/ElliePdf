using ElliePdf.Pdf.Contracts;
using ElliePdf.Services;

namespace ElliePdf.ViewModels;

public sealed class SearchResultItemViewModel
{
    public SearchResultItemViewModel(SearchResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        PageLabel = AppResources.Format("Reader_SearchResultPage", result.PageIndex + 1);
        Context = NormalizeContext(result.Context);
        AutomationName = AppResources.Format("Reader_SearchResultName", result.PageIndex + 1, Context);
    }

    public SearchResult Result { get; }

    public string PageLabel { get; }

    public string Context { get; }

    public string AutomationName { get; }

    private static string NormalizeContext(string value) =>
        string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
}

public sealed record DocumentPropertiesViewModel(
    string Title,
    string Author,
    string Subject,
    string Creator,
    string PdfVersion,
    string PageCount,
    string PageSize,
    string Security,
    string Permissions,
    string Forms,
    string Outline);
