namespace ElliePdf.Application.Ports;

public sealed record DialogRequest(string Title, string Message, IReadOnlyList<string> Actions);

public sealed record DialogResult(string Action, bool WasCancelled = false);

public interface IUserDialogPort
{
    ValueTask<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken);
}

public sealed record FilePickerRequest(string? SuggestedName = null, IReadOnlyList<string>? AllowedExtensions = null);

public sealed record FilePickerResult(string? Path, bool WasCancelled = false);

public interface IFilePickerPort
{
    ValueTask<FilePickerResult> PickFileAsync(FilePickerRequest request, CancellationToken cancellationToken);
}

public interface INavigationPort
{
    ValueTask NavigateAsync(string route, object? parameter, CancellationToken cancellationToken);
}

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record Notification(string Title, string Message, NotificationKind Kind = NotificationKind.Information);

public interface INotificationPort
{
    ValueTask NotifyAsync(Notification notification, CancellationToken cancellationToken);
}
