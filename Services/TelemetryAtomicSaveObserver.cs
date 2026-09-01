using System.Collections.Concurrent;
using ElliePdf.Infrastructure.Storage;
using ElliePdf.Telemetry;

namespace ElliePdf.Services;

/// <summary>
/// Converts the integrity store's stage callbacks into measurement-only ETW
/// events. Transaction identifiers stay inside this process and are never logged.
/// </summary>
internal sealed class TelemetryAtomicSaveObserver : IAtomicSaveLifecycleObserver
{
    private readonly ConcurrentDictionary<string, StageActivity> _activities =
        new(StringComparer.Ordinal);

    public ValueTask OnStageAsync(
        AtomicSaveStage stage,
        string transactionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = TelemetryOperation.StartTimestamp();
        var current = _activities.AddOrUpdate(
            transactionId,
            static (_, state) => state,
            static (_, previous, state) =>
            {
                ElliePdfEventSource.Log.SaveStageCompleted(
                    previous.OperationId,
                    (int)previous.Stage,
                    TelemetryOperation.ElapsedMicroseconds(previous.Started));
                return state with { OperationId = previous.OperationId };
            },
            new StageActivity(TelemetryOperation.NextId(), stage, now));

        ElliePdfEventSource.Log.SaveStageStarted(current.OperationId, (int)stage);
        ElliePdfEventSource.Log.SaveStage(current.OperationId, (int)stage, 0, true);

        if (stage == AtomicSaveStage.CleanupCompleted
            && _activities.TryRemove(transactionId, out var completed))
        {
            var duration = TelemetryOperation.ElapsedMicroseconds(completed.Started);
            ElliePdfEventSource.Log.SaveStageCompleted(
                completed.OperationId,
                (int)completed.Stage,
                duration);
            ElliePdfEventSource.Log.SaveStage(
                completed.OperationId,
                (int)completed.Stage,
                duration,
                true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnFailedAsync(
        AtomicSaveFailureKind failureKind,
        string transactionId)
    {
        if (_activities.TryRemove(transactionId, out var failed))
        {
            var duration = TelemetryOperation.ElapsedMicroseconds(failed.Started);
            ElliePdfEventSource.Log.SaveStage(
                failed.OperationId,
                (int)failed.Stage,
                duration,
                false);
            ElliePdfEventSource.Log.SaveFailed(
                failed.OperationId,
                (int)failureKind);
        }

        return ValueTask.CompletedTask;
    }

    private sealed record StageActivity(
        int OperationId,
        AtomicSaveStage Stage,
        long Started);
}
