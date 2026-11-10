using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Apm.Agent;

var results = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); results.Add(name); }
var root = Path.Combine(Path.GetTempPath(), "apm-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
SpanData Span(string instance = "first", bool wide = false) => new()
{
    TraceId = Guid.NewGuid().ToString("N"), SpanId = Guid.NewGuid().ToString("N")[..16],
    InstanceId = instance, ServiceName = "RecoveryChecks", RunId = "recovery", Name = wide ? new string('가', 160) : "Request",
    CodeVersion = wide ? new string('나', 200) : "test", UserKey = wide ? new string('다', 100) : null,
    SessionId = wide ? new string('라', 100) : null, Category = "packet", IsEntry = true,
    StartTime = DateTime.UtcNow, Outcome = "success", DetailStatus = "sampled_out"
};
AgentOptions Options(string path, int count = 8192) => new()
{
    ServiceName = "RecoveryChecks", SpoolDirectory = path, RequestQueueCapacity = 2,
    MaxSpoolBatches = count, MaxAttempts = 1, RequestTimeout = TimeSpan.FromMilliseconds(40),
    FlushInterval = TimeSpan.FromMilliseconds(5)
};
var receipts = new ConcurrentDictionary<string, TelemetryBatch>();
var seen = new ConcurrentQueue<string>();
var lostAcknowledgement = new Handler(async (request, ct) =>
{
    var batch = JsonSerializer.Deserialize<TelemetryBatch>(await request.Content!.ReadAsByteArrayAsync(ct), json)!;
    receipts.TryAdd(batch.BatchId, batch); seen.Enqueue(batch.BatchId);
    await Task.Delay(10000, ct); // Collector committed; acknowledgement never reached the agent.
    return new(HttpStatusCode.Accepted);
});
await using (var sender = new TelemetrySender(Options(root), "first", lostAcknowledgement))
{
    for (var i = 0; i < 128; i++) Check(sender.Enqueue(Span()), "accepted request " + i);
    await Task.Delay(150);
    Check(sender.Health.RequestsDropped == 0 && sender.Health.SpoolPendingRequests > 0, "overflow is retained on disk");
    try
    {
        await using var conflicting = new TelemetrySender(Options(root), "other");
        Check(false, "exclusive spool lease");
    }
    catch (IOException) { Check(true, "exclusive spool lease"); }
}
var filesBefore = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length;
Check(filesBefore > 0, "pending batches survive sender shutdown");
await using (var sender = new TelemetrySender(Options(root), "second", new Handler(async (request, ct) =>
{
    var batch = JsonSerializer.Deserialize<TelemetryBatch>(await request.Content!.ReadAsByteArrayAsync(ct), json)!;
    seen.Enqueue(batch.BatchId);
    return new(receipts.TryAdd(batch.BatchId, batch) ? HttpStatusCode.Accepted : HttpStatusCode.OK);
})))
{
    var until = DateTime.UtcNow.AddSeconds(15);
    while (sender.Health.SpoolPendingBatches > 0 && DateTime.UtcNow < until) await Task.Delay(20);
    Check(sender.Health.SpoolPendingBatches == 0 && sender.Health.SpoolBytes == 0, "restart replay drains acknowledged files");
    Check(sender.Health.ReplayedRequests == 128 && sender.Health.RequestsDropped == 0, "all overflow and failed HTTP requests recover");
    Check(sender.Health.RequestsSent == 0, "previous instance replay does not inflate new instance requests sent");
}
Check(receipts.Values.Sum(b => b.Metrics.Count) == 128, "lost acknowledgement replay keeps batch IDs and exactly once metrics");
Check(receipts.Values.SelectMany(b => b.Metrics).Select(m => m.TraceId).Distinct().Count() == 128, "all original trace identities preserved");
Check(seen.Count > receipts.Count, "test exercised duplicate delivery");

await using (var sender = new TelemetrySender(Options(Path.Combine(root, "bounded"), 1), "first",
    new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
{
    for (var i = 0; i < 40; i++) sender.Enqueue(Span());
    Check(sender.Health.SpoolPendingBatches <= 1 && sender.Health.SpoolErrors > 0 && sender.Health.RequestsDropped > 0,
        "disk budget exhaustion is bounded and explicitly reports loss");
}

var wire = new ConcurrentQueue<(int Bytes, TelemetryBatch Batch)>();
await using (var sender = new TelemetrySender(new AgentOptions { RequestQueueCapacity = 4096 }, "first",
    new Handler(async (request, ct) =>
    {
        var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
        wire.Enqueue((bytes.Length, JsonSerializer.Deserialize<TelemetryBatch>(bytes, json)!));
        return new(HttpStatusCode.Accepted);
    })))
{
    for (var i = 0; i < 2048; i++) sender.Enqueue(Span());
    for (var i = 0; i < 1024; i++) sender.Enqueue(Span(wide: true));
}
Check(wire.Max(b => b.Batch.Spans.Count) > 256 && wire.Max(b => b.Batch.Spans.Count) <= 1024, "larger batches stay within item limit");
Check(wire.All(b => b.Bytes <= 2_000_000 && b.Batch.Logs.Count <= 1024), "escaped Unicode batches split within HTTP byte limit");
Check(wire.Sum(b => b.Batch.Metrics.Count) == 3072, "byte splitting preserves every request metric");
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, assertions = results.Count, evidence = root, tests = results.Where(r => !r.StartsWith("accepted request")) }, new JsonSerializerOptions { WriteIndented = true }));

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
