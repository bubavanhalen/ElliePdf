using CommunityToolkit.Mvvm.ComponentModel;

namespace ElliePdf.ViewModels;

public sealed partial class DocumentTabItemViewModel : ObservableObject
{
    public DocumentTabItemViewModel(Guid tabId, string title)
    {
        TabId = tabId;
        Title = title;
    }

    public Guid TabId { get; }

    [ObservableProperty]
    public partial string Title { get; private set; }
}
