using ElliePdf;

namespace ElliePdf.Services;

public sealed class UserSettings
{
    public PdfZoomMode DefaultZoomMode { get; set; } = PdfZoomMode.FitWidth;

    public bool ConfirmOverwriteSave { get; set; } = true;

    public bool ConfirmOrganizeSave { get; set; } = true;

    public bool AutoSaveCompanion { get; set; } = true;

    public int RecentFilesMaxCount { get; set; } = 12;

    public string AppTheme { get; set; } = "System";
}
