using CommunityToolkit.Mvvm.ComponentModel;

namespace ElliePdf.ViewModels;

public sealed partial class DocumentTabItemViewModel : ObservableObject
{
    public DocumentTabItemViewModel(Guid tabId, string title, bool isDirty)
    {
        TabId = tabId;
        Title = title;
        IsDirty = isDirty;
    }

    public Guid TabId { get; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }
}
