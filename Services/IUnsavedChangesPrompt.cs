namespace ElliePdf.Services;

public enum UnsavedChangesChoice
{
    Cancel,
    Discard,
    Save
}

public interface IUnsavedChangesPrompt
{
    Task<UnsavedChangesChoice> PromptAsync(string fileName, CancellationToken cancellationToken = default);
}
