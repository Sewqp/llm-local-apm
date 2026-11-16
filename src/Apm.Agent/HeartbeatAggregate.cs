using System.Text.Json;

namespace Apm.Agent;

// Only successful, fast, unselected packet Heartbeats enter this stream. Exceptional
// requests remain in Metrics/Spans; the two streams are disjoint, not sampled estimates.
public sealed class HeartbeatAggregate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; }
    public string ServiceName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string CodeVersion { get; set; } = "";
    public long Count { get; set; }
    public double DurationSumMs { get; set; }
    public double MinDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    // Non-cumulative counts; bounds are fixed by this wire version. No user/session IDs.
    public string Histogram { get; set; } = "[]";
    public static readonly IReadOnlyList<double> Bounds = Array.AsReadOnly(new double[]
        { .01, .025, .05, .1, .25, .5, 1, 2.5, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 86400000 });
    public static int Bin(double duration)
    {
        for (var i = 0; i < Bounds.Count; i++) if (duration <= Bounds[i]) return i;
        return Bounds.Count - 1;
    }
}

internal sealed class HeartbeatAccumulator(AgentOptions options, string instanceId, TelemetrySender sender)
{
    private readonly object _gate = new();
    private readonly Dictionary<long, (HeartbeatAggregate Row, long[] Bins)> _buckets = new();
    private const long WindowTicks = 5 * TimeSpan.TicksPerSecond;
    public void Record(SpanData root)
    {
        lock (_gate)
        {
            var ticks = root.StartTime.Ticks / WindowTicks * WindowTicks;
            if (!_buckets.TryGetValue(ticks, out var item))
            {
                // Bound memory even after a wall-clock adjustment. Each fragment has its own ID.
                if (_buckets.Count >= 4) FlushLocked(true);
                item = (new HeartbeatAggregate { Timestamp = new DateTime(ticks, DateTimeKind.Utc),
                    ServiceName = options.ServiceName, InstanceId = instanceId, RunId = options.RunId,
                    CodeVersion = options.CodeVersion, MinDurationMs = root.DurationMs }, new long[HeartbeatAggregate.Bounds.Count]);
                _buckets.Add(ticks, item);
            }
            item.Row.Count++; item.Row.DurationSumMs += root.DurationMs;
            item.Row.MinDurationMs = Math.Min(item.Row.MinDurationMs, root.DurationMs);
            item.Row.MaxDurationMs = Math.Max(item.Row.MaxDurationMs, root.DurationMs);
            item.Bins[HeartbeatAggregate.Bin(root.DurationMs)]++;
            sender.RecordAggregation();
        }
    }
    public void Flush(bool force) { lock (_gate) FlushLocked(force); }
    private void FlushLocked(bool force)
    {
        var cutoff = DateTime.UtcNow.Ticks - WindowTicks;
        foreach (var key in _buckets.Keys.Where(k => force || k <= cutoff).ToArray())
        {
            var item = _buckets[key]; _buckets.Remove(key);
            item.Row.Histogram = JsonSerializer.Serialize(item.Bins);
            sender.EnqueueAggregate(item.Row);
        }
    }
}
