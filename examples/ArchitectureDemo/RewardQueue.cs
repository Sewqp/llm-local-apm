using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Apm.Agent;

namespace ArchitectureDemo;

public sealed record RewardJob(string Id, ActivityContext Cause, string? UserKey, string SessionId, DateTime EnqueuedAt, bool Fail);
public sealed record JobState(string Id, string Status, string? TraceId = null);

public sealed class RewardQueue
{
    // Admission is bounded. This is an in-memory example, not a durable message broker.
    public Channel<RewardJob> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<RewardJob>(
        new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, JobState> _states = new();
    private readonly object _admission = new();
    private long _accepted, _rejected, _completed, _failed;
    public IResult Enqueue(HttpContext ctx, LocalTelemetry telemetry)
    {
        lock (_admission)
        {
            var id = Guid.NewGuid().ToString("N");
            var job = new RewardJob(id, Activity.Current!.Context, telemetry.UserKey((long)ctx.Items["user"]!),
                ((Guid)ctx.Items["session"]!).ToString("N"), DateTime.UtcNow, ctx.Request.Query["fail"] == "true");
            if (_states.Count >= 4096) { _rejected++; return Results.StatusCode(429); }
            _states[id] = new(id, "queued");
            if (!Channel.Writer.TryWrite(job))
            { _states.TryRemove(id, out _); _rejected++; return Results.StatusCode(429); }
            _accepted++;
            return Results.Accepted($"/jobs/{id}", new { id });
        }
    }
    public IResult Find(string id) => _states.TryGetValue(id, out var state) ? Results.Ok(state) : Results.NotFound();
    public void Finish(RewardJob job, string traceId, bool failed)
    {
        _states[job.Id] = new(job.Id, failed ? "failed" : "completed", traceId);
        if (failed) Interlocked.Increment(ref _failed); else Interlocked.Increment(ref _completed);
    }
    public object Snapshot() => new { accepted = Interlocked.Read(ref _accepted), rejected = Interlocked.Read(ref _rejected),
        completed = Interlocked.Read(ref _completed), failed = Interlocked.Read(ref _failed), queued = Channel.Reader.Count };
}
