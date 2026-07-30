namespace ElliePdf.ViewModels;

public sealed class RecentFileItemViewModel
{
    public RecentFileItemViewModel(string filePath)
    {
        FilePath = filePath;
        DisplayName = Path.GetFileName(filePath);
        Location = Path.GetDirectoryName(filePath) ?? string.Empty;
    }

    public string FilePath { get; }

    public string DisplayName { get; }

    public string Location { get; }
}
