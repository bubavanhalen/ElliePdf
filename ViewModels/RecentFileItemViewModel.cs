namespace ElliePdf.ViewModels;

public sealed class RecentFileItemViewModel
{
    public RecentFileItemViewModel(string filePath)
    {
        FilePath = filePath;
        DisplayName = Path.GetFileName(filePath);
    }

    public string FilePath { get; }

    public string DisplayName { get; }
}
