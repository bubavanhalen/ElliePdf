using System.Diagnostics.Tracing;

namespace ElliePdf.Telemetry;

/// <summary>Stable, privacy-safe ETW contract. Events contain measurements and opaque IDs only.</summary>
[EventSource(Name = "ElliePdf")]
public sealed class ElliePdfEventSource : EventSource
{
    public static readonly ElliePdfEventSource Log = new();
    private ElliePdfEventSource() { }

    [Event(1, Level = EventLevel.Informational)] public void ActivationStart(int operationId) => WriteEventCoreInt32(1, operationId);
    [Event(2, Level = EventLevel.Informational)] public void ActivationStop(int operationId, long durationMicroseconds, bool success) => WriteEventCoreInt32Int64Boolean(2, operationId, durationMicroseconds, success);
    [Event(3, Level = EventLevel.Informational)] public void OpenStart(int operationId) => WriteEventCoreInt32(3, operationId);
    [Event(4, Level = EventLevel.Informational)] public void OpenStop(int operationId, long durationMicroseconds, bool success) => WriteEventCoreInt32Int64Boolean(4, operationId, durationMicroseconds, success);
    [Event(5, Level = EventLevel.Informational)] public void MetadataRead(int operationId, long durationMicroseconds, int pageCount) => WriteEventCoreInt32Int64Int32(5, operationId, durationMicroseconds, pageCount);
    [Event(6, Level = EventLevel.Informational)] public void RenderQueueWait(int operationId, long durationMicroseconds) => WriteEventCoreInt32Int64(6, operationId, durationMicroseconds);
    [Event(7, Level = EventLevel.Informational)] public void NativeRender(int operationId, long durationMicroseconds, int pixelWidth, int pixelHeight, bool success) => WriteEventCoreInt32Int64Int32Int32Boolean(7, operationId, durationMicroseconds, pixelWidth, pixelHeight, success);
    [Event(8, Level = EventLevel.Informational)] public void PixelUpload(int operationId, long durationMicroseconds, long bytes) => WriteEventCoreInt32Int64Int64(8, operationId, durationMicroseconds, bytes);
    [Event(9, Level = EventLevel.Informational)] public void FirstPagePresented(int operationId, long durationMicroseconds) => WriteEventCoreInt32Int64(9, operationId, durationMicroseconds);
    [Event(10, Level = EventLevel.Informational)] public void CacheHit(int operationId, long bytes) => WriteEventCoreInt32Int64(10, operationId, bytes);
    [Event(11, Level = EventLevel.Informational)] public void CacheEviction(int operationId, long bytes, int reason) => WriteEventCoreInt32Int64Int32(11, operationId, bytes, reason);
    [Event(12, Level = EventLevel.Informational)] public void Search(int operationId, long durationMicroseconds, int resultCount) => WriteEventCoreInt32Int64Int32(12, operationId, durationMicroseconds, resultCount);
    [Event(13, Level = EventLevel.Informational)] public void SaveStage(int operationId, int stage, long durationMicroseconds, bool success) => WriteEventCoreInt32Int32Int64Boolean(13, operationId, stage, durationMicroseconds, success);
    [Event(14, Level = EventLevel.Error)] public void WorkerFailure(int operationId, int errorCode) => WriteEventCoreInt32Int32(14, operationId, errorCode);

    // The names below are the normative WP-01 contract. Keep the original compact
    // events above for compatibility with early preview traces; these events are
    // deliberately measurement-only and accept no document-derived strings.
    [Event(15)] public void AppLaunchStart(int operationId) => WriteEventCoreInt32(15, operationId);
    [Event(16)] public void ShellInteractive(int operationId, long durationMicroseconds) => WriteEventCoreInt32Int64(16, operationId, durationMicroseconds);
    [Event(17)] public void ActivationReceived(int operationId) => WriteEventCoreInt32(17, operationId);
    [Event(18)] public void DocumentOpenStart(int operationId) => WriteEventCoreInt32(18, operationId);
    [Event(19)] public void MetadataReady(int operationId, long durationMicroseconds, int pageCount) => WriteEventCoreInt32Int64Int32(19, operationId, durationMicroseconds, pageCount);
    [Event(20)] public void FirstPageRequested(int operationId) => WriteEventCoreInt32(20, operationId);
    [Event(21)] public void RenderQueued(int operationId, int priority) => WriteEventCoreInt32Int32(21, operationId, priority);
    [Event(22)] public void RenderStarted(int operationId, int pixelWidth, int pixelHeight) => WriteEventCoreInt32Int32Int32(22, operationId, pixelWidth, pixelHeight);
    [Event(23)] public void RenderCompleted(int operationId, long durationMicroseconds, long bytes) => WriteEventCoreInt32Int64Int64(23, operationId, durationMicroseconds, bytes);
    [Event(24)] public void RenderCancelled(int operationId) => WriteEventCoreInt32(24, operationId);
    [Event(25)] public void RenderRejectedAsStale(int operationId) => WriteEventCoreInt32(25, operationId);
    [Event(26)] public void PdfiumLaneWait(int operationId, long durationMicroseconds) => WriteEventCoreInt32Int64(26, operationId, durationMicroseconds);
    [Event(27)] public void PdfiumCallDuration(int operationId, long durationMicroseconds, int callKind) => WriteEventCoreInt32Int64Int32(27, operationId, durationMicroseconds, callKind);
    [Event(28)] public void FramePresented(int operationId, long durationMicroseconds) => WriteEventCoreInt32Int64(28, operationId, durationMicroseconds);
    [Event(29)] public void CacheMiss(int operationId) => WriteEventCoreInt32(29, operationId);
    [Event(30)] public void CacheBytes(int operationId, long bytes) => WriteEventCoreInt32Int64(30, operationId, bytes);
    [Event(31)] public void SearchStarted(int operationId) => WriteEventCoreInt32(31, operationId);
    [Event(32)] public void SearchPageCompleted(int operationId, int pageIndex, long durationMicroseconds, int resultCount) => WriteEventCoreInt32Int32Int64Int32(32, operationId, pageIndex, durationMicroseconds, resultCount);
    [Event(33)] public void SearchResultPublished(int operationId, int resultCount) => WriteEventCoreInt32Int32(33, operationId, resultCount);
    [Event(34)] public void SearchCancelled(int operationId) => WriteEventCoreInt32(34, operationId);
    [Event(35)] public void SaveStageStarted(int operationId, int stage) => WriteEventCoreInt32Int32(35, operationId, stage);
    [Event(36)] public void SaveStageCompleted(int operationId, int stage, long durationMicroseconds) => WriteEventCoreInt32Int32Int64(36, operationId, stage, durationMicroseconds);
    [Event(37, Level = EventLevel.Error)] public void SaveFailed(int operationId, int errorCode) => WriteEventCoreInt32Int32(37, operationId, errorCode);
    [Event(38)] public void RecoveryCheckpointed(int operationId, long durationMicroseconds) => WriteEventCoreInt32Int64(38, operationId, durationMicroseconds);
    [Event(39)] public void WorkerStarted(int operationId) => WriteEventCoreInt32(39, operationId);
    [Event(40)] public void WorkerRestarted(int operationId, int restartCount) => WriteEventCoreInt32Int32(40, operationId, restartCount);
    [Event(41, Level = EventLevel.Warning)] public void WorkerBudgetExceeded(int operationId, int budgetKind) => WriteEventCoreInt32Int32(41, operationId, budgetKind);
    [Event(42, Level = EventLevel.Error)] public void WorkerCrashed(int operationId, int exitCode) => WriteEventCoreInt32Int32(42, operationId, exitCode);
    [Event(43)] public void PixelUploadDuration(int operationId, long durationMicroseconds, long bytes) => WriteEventCoreInt32Int64Int64(43, operationId, durationMicroseconds, bytes);
    [Event(44)] public void CacheEvicted(int operationId, long bytes, int reason) => WriteEventCoreInt32Int64Int32(44, operationId, bytes, reason);

    [NonEvent]
    private unsafe void WriteEventCoreInt32(int eventId, int arg1)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[1];
        SetEventData(ref data[0], &arg1, sizeof(int));
        WriteEventCore(eventId, 1, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int32(int eventId, int arg1, int arg2)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[2];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(int));
        WriteEventCore(eventId, 2, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int64(int eventId, int arg1, long arg2)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[2];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(long));
        WriteEventCore(eventId, 2, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int64Boolean(int eventId, int arg1, long arg2, bool arg3)
    {
        if (!IsEnabled())
            return;

        int booleanValue = arg3 ? 1 : 0;
        EventData* data = stackalloc EventData[3];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(long));
        SetEventData(ref data[2], &booleanValue, sizeof(int));
        WriteEventCore(eventId, 3, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int64Int32(int eventId, int arg1, long arg2, int arg3)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[3];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(long));
        SetEventData(ref data[2], &arg3, sizeof(int));
        WriteEventCore(eventId, 3, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int64Int64(int eventId, int arg1, long arg2, long arg3)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[3];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(long));
        SetEventData(ref data[2], &arg3, sizeof(long));
        WriteEventCore(eventId, 3, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int32Int32(int eventId, int arg1, int arg2, int arg3)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[3];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(int));
        SetEventData(ref data[2], &arg3, sizeof(int));
        WriteEventCore(eventId, 3, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int32Int64(int eventId, int arg1, int arg2, long arg3)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[3];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(int));
        SetEventData(ref data[2], &arg3, sizeof(long));
        WriteEventCore(eventId, 3, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int32Int64Boolean(int eventId, int arg1, int arg2, long arg3, bool arg4)
    {
        if (!IsEnabled())
            return;

        int booleanValue = arg4 ? 1 : 0;
        EventData* data = stackalloc EventData[4];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(int));
        SetEventData(ref data[2], &arg3, sizeof(long));
        SetEventData(ref data[3], &booleanValue, sizeof(int));
        WriteEventCore(eventId, 4, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int32Int64Int32(int eventId, int arg1, int arg2, long arg3, int arg4)
    {
        if (!IsEnabled())
            return;

        EventData* data = stackalloc EventData[4];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(int));
        SetEventData(ref data[2], &arg3, sizeof(long));
        SetEventData(ref data[3], &arg4, sizeof(int));
        WriteEventCore(eventId, 4, data);
    }

    [NonEvent]
    private unsafe void WriteEventCoreInt32Int64Int32Int32Boolean(int eventId, int arg1, long arg2, int arg3, int arg4, bool arg5)
    {
        if (!IsEnabled())
            return;

        int booleanValue = arg5 ? 1 : 0;
        EventData* data = stackalloc EventData[5];
        SetEventData(ref data[0], &arg1, sizeof(int));
        SetEventData(ref data[1], &arg2, sizeof(long));
        SetEventData(ref data[2], &arg3, sizeof(int));
        SetEventData(ref data[3], &arg4, sizeof(int));
        SetEventData(ref data[4], &booleanValue, sizeof(int));
        WriteEventCore(eventId, 5, data);
    }

    [NonEvent]
    private static unsafe void SetEventData(ref EventData data, void* value, int size)
    {
        data.DataPointer = (nint)value;
        data.Size = size;
    }
}
