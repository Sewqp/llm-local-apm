using Apm.Agent;

namespace ArchitectureDemo;

public sealed class RewardWorker(RewardQueue queue, CatalogStore store, LocalTelemetry telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Channel.Reader.ReadAllAsync(stoppingToken))
        {
            var started = DateTime.UtcNow;
            using var activity = telemetry.StartJob("RewardJob", job.UserKey, job.SessionId, job.Cause, "RewardWorker.cs");
            // Root duration is enqueue-to-finish. The explicit wait span separates queue delay from processing.
            activity!.SetStartTime(job.EnqueuedAt);
            using (var wait = LocalTelemetry.StartSpan("queue.wait", "wait"))
            { wait!.SetStartTime(job.EnqueuedAt); wait.SetEndTime(started); }
            var failed = false;
            try
            {
                using (LocalTelemetry.StartSpan("simulation.worker.wait", "wait")) await Task.Delay(80, stoppingToken);
                if (job.Fail) throw new InvalidOperationException("SECRET_JOB_DO_NOT_EXPORT");
                store.Grant(job.Id);
                LocalTelemetry.SetOutcome("success");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { LocalTelemetry.SetOutcome("cancelled"); failed = true; }
            catch (Exception ex) { LocalTelemetry.RecordException(ex); failed = true; }
            queue.Finish(job, activity.TraceId.ToHexString(), failed);
        }
    }
}
