using ElliePdf;

namespace ElliePdf.Services;

public sealed class UserSettings
{
    public PdfZoomMode DefaultZoomMode { get; set; } = PdfZoomMode.FitWidth;

    public bool ConfirmOverwriteSave { get; set; } = true;

    public bool ConfirmOrganizeSave { get; set; } = true;

    public bool AutoSaveCompanion { get; set; } = true;

    public bool EnableLabs { get; set; }

    public int RecentFilesMaxCount { get; set; } = 12;

    public bool ReopenLastSession { get; set; } = true;

    public bool KeepRecentFiles { get; set; } = true;

    public bool PersistViewState { get; set; } = true;

    public bool EnableLocalDiagnostics { get; set; }

    public bool EnableCrashReports { get; set; }
}
