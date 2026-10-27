using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apm.Agent;

var results = new List<object>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    results.Add(new { test = name, passed = true });
}
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var captured = new ConcurrentQueue<TelemetryBatch>();
var handler = new CaptureHandler(async (request, ct) =>
{
    captured.Enqueue((await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
});
var options = new AgentOptions { ServiceName = "AgentChecks", DetailSampleRate = 1, FlushInterval = TimeSpan.FromMilliseconds(10), UserHashKey = new byte[32] };
await using (var agent = new LocalTelemetry(options, handler))
{
    var pseudo = agent.UserKey(123456);
    Check(pseudo is { Length: 24 } && pseudo == agent.UserKey(123456) && pseudo != agent.UserKey(654321), "stable keyed pseudonym");
    using (agent.StartRequest("Synthetic.Normal", Guid.NewGuid(), 123456))
    {
        using (LocalTelemetry.StartSpan("session.lock_check", "wait")) await Task.Delay(20);
        using (LocalTelemetry.StartSpan("handler", "handler"))
        {
            using (LocalTelemetry.StartSpan("response.send", "network")) await Task.Delay(5);
            LocalTelemetry.SetOutcome("success", "accepted");
        }
    }
    using (agent.StartRequest("Synthetic.Failure", Guid.NewGuid(), 123456))
    {
        try { throw new InvalidOperationException("SECRET_TOKEN_DO_NOT_EXPORT"); }
        catch (Exception ex) { LocalTelemetry.RecordException(ex); }
    }
}
List<SpanData> StoredSpans(IEnumerable<TelemetryBatch> batches) => batches.SelectMany(b => b.Spans)
    .GroupBy(s => (s.TraceId, s.SpanId)).Select(g => g.LastOrDefault(s => s.DetailStatus != "pending") ?? g.First()).ToList();
var spans = StoredSpans(captured);
var root = spans.Single(s => s.Name == "Synthetic.Normal");
var child = spans.Single(s => s.Name == "handler");
Check(child.TraceId == root.TraceId && child.ParentSpanId == root.SpanId, "async parent context");
Check(root.DurationMs >= 20 && root.Outcome == "success", "request includes pre-handler wait and domain result");
Check(captured.SelectMany(b => b.Metrics).Count() == 2, "only entries produce request metrics");
Check(captured.SelectMany(b => b.Logs).Single().ErrorType == typeof(InvalidOperationException).FullName, "exception correlated to trace");
Check(!JsonSerializer.Serialize(captured).Contains("SECRET_TOKEN_DO_NOT_EXPORT"), "exception message redacted");

var attemptIds = new ConcurrentQueue<string>();
var attempts = 0;
await using (var sender = new TelemetrySender(options, "test", new CaptureHandler(async (request, ct) =>
{
    var batch = await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct);
    attemptIds.Enqueue(batch!.BatchId);
    return new HttpResponseMessage(Interlocked.Increment(ref attempts) == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted);
})))
{
    sender.Enqueue(root);
    await Task.Delay(500);
    Check(sender.Health.Sent == 1 && sender.Health.FailedBatches == 1, "transient HTTP failure retries");
}
Check(attemptIds.Count == 2 && attemptIds.Distinct().Count() == 1, "retry keeps idempotency key");

await using (var sender = new TelemetrySender(new AgentOptions
{
    QueueCapacity = 4, RequestQueueCapacity = 4, BatchSize = 1, MaxAttempts = 1, FlushInterval = TimeSpan.FromMilliseconds(10), RequestTimeout = TimeSpan.FromMilliseconds(80)
}, "test", new CaptureHandler(async (_, ct) =>
{
    await Task.Delay(10000, ct);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    for (var i = 0; i < 1000; i++) sender.Enqueue(root);
    Check(sender.Health.QueueDepth <= 4 && sender.Health.Dropped > 0, "bounded nonblocking queue on collector outage");
    await Task.Delay(200);
    Check(sender.Health.FailedBatches > 0, "request timeout visible in health");
}

try
{
    await using var invalid = new TelemetrySender(new AgentOptions { Endpoint = new Uri("https://example.com") }, "test");
    Check(false, "external endpoint blocked");
}
catch (ArgumentException) { Check(true, "external endpoint blocked"); }

try
{
    await using var invalid = new LocalTelemetry(new AgentOptions { UserHashKey = new byte[8] });
    Check(false, "short pseudonym key rejected");
}
catch (ArgumentException) { Check(true, "short pseudonym key rejected"); }

var sampled = new ConcurrentQueue<TelemetryBatch>();
await using (var agent = new LocalTelemetry(new AgentOptions
{
    DetailSampleRate = 0, SlowTraceMs = 40, FlushInterval = TimeSpan.FromMilliseconds(5)
}, new CaptureHandler(async (request, ct) =>
{
    sampled.Enqueue((await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    using (agent.StartRequest("Sample.Normal", Guid.NewGuid(), 1))
    { using var inner = LocalTelemetry.StartSpan("normal.child"); LocalTelemetry.SetOutcome("success"); }
    using (agent.StartRequest("Sample.Slow", Guid.NewGuid(), 1))
    { using var inner = LocalTelemetry.StartSpan("slow.child"); await Task.Delay(60); LocalTelemetry.SetOutcome("success"); }
    using (agent.StartRequest("Sample.Error", Guid.NewGuid(), 1))
    {
        using (LocalTelemetry.StartSpan("error.child")) { }
        LocalTelemetry.RecordException(new InvalidOperationException("DO_NOT_SEND"));
    }
}
var sampledSpans = StoredSpans(sampled);
Check(sampled.SelectMany(b => b.Metrics).Count() == 3, "sampling preserves every request metric");
Check(sampledSpans.Single(s => s.Name == "Sample.Normal").DetailStatus == "sampled_out" &&
      sampledSpans.All(s => s.Name != "normal.child"), "normal detail omission is explicit at zero sampling");
Check(sampledSpans.Single(s => s.Name == "Sample.Slow").SamplingReason == "slow" &&
      sampledSpans.Any(s => s.Name == "slow.child"), "slow request details retained without random sampling");
Check(sampledSpans.Single(s => s.Name == "Sample.Error").SamplingReason == "error" &&
      sampledSpans.Any(s => s.Name == "error.child") && sampled.SelectMany(b => b.Logs).Count() == 1,
      "error details and correlated log retained without random sampling");

var limited = new ConcurrentQueue<TelemetryBatch>();
await using (var agent = new LocalTelemetry(new AgentOptions
{
    DetailSampleRate = 1, MaxDetailSpansPerTrace = 2, FlushInterval = TimeSpan.FromMilliseconds(5)
}, new CaptureHandler(async (request, ct) =>
{
    limited.Enqueue((await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    using (agent.StartRequest("Bounded", Guid.NewGuid(), 1))
        for (var i = 0; i < 10; i++) { using var childSpan = LocalTelemetry.StartSpan("child"); }
    using (LocalTelemetry.StartSpan("uncorrelated")) { }
    Check(agent.Sender.Health.UncorrelatedSpansSkipped == 1, "unrelated dependency work does not fill telemetry queues");
}
var limitedRoot = StoredSpans(limited).Single(s => s.IsEntry);
Check(limitedRoot.DetailStatus == "truncated" && limitedRoot.ExpectedDetailSpans == 2 && limitedRoot.LimitedDetailSpans == 8,
    "large trace is bounded and explicitly marked incomplete");

var delayed = new ConcurrentQueue<TelemetryBatch>();
await using (var agent = new LocalTelemetry(new AgentOptions
{
    DetailSampleRate = 1, FlushInterval = TimeSpan.FromMilliseconds(5)
}, new CaptureHandler(async (request, ct) =>
{
    delayed.Enqueue((await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    ActivityContext context;
    using (var request = agent.StartRequest("Delayed.Redis", Guid.NewGuid(), 11))
    { context = request!.Context; LocalTelemetry.SetOutcome("success"); }
    await Task.Delay(100);
    using var redis = new ActivitySource("OpenTelemetry.Instrumentation.StackExchangeRedis.Test");
    using (redis.StartActivity("late.profile", ActivityKind.Client, context)) { }
}
var delayedSpans = StoredSpans(delayed);
Check(delayed.SelectMany(b => b.Metrics).Count() == 1 && delayedSpans.Count == 2 &&
      delayedSpans.Single(s => s.IsEntry).ExpectedDetailSpans == 1 &&
      delayedSpans.Any(s => s.Category == "redis"), "late dependency spans retained without duplicate request metrics");

var coreReceived = 0; var healthReceived = 0;
await using (var sender = new TelemetrySender(new AgentOptions
{
    QueueCapacity = 4, RequestQueueCapacity = 256, FlushInterval = TimeSpan.FromMilliseconds(5),
    RequestTimeout = TimeSpan.FromSeconds(6), MaxAttempts = 1
}, "isolation-check", new CaptureHandler(async (request, ct) =>
{
    var batch = (await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!;
    if (batch.Metrics.Count == 0 && batch.Runtime.Count == 0) await Task.Delay(10000, ct);
    Interlocked.Add(ref coreReceived, batch.Metrics.Count);
    Interlocked.Add(ref healthReceived, batch.Runtime.Count);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    var detailSpan = new SpanData { IsEntry = false };
    for (var i = 0; i < 1000; i++) sender.Enqueue(detailSpan);
    for (var i = 0; i < 100; i++) sender.Enqueue(root);
    await Task.Delay(5500);
    Check(coreReceived == 100 && sender.Health.RequestsDropped == 0 && sender.Health.DetailsDropped > 0,
        "saturated detail queue cannot drop request metrics");
    Check(healthReceived > 0, "runtime bypasses a blocked detail sender");
}

var firstDetail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseDetail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var detailedCount = 0;
await using (var sender = new TelemetrySender(new AgentOptions
{
    BatchSize = 64, QueueCapacity = 4096, FlushInterval = TimeSpan.FromSeconds(3), RequestTimeout = TimeSpan.FromSeconds(10)
}, "detail-batching-check", new CaptureHandler(async (request, ct) =>
{
    var batch = (await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!;
    if (Interlocked.Add(ref detailedCount, batch.Spans.Count) == 64)
    { firstDetail.TrySetResult(); await releaseDetail.Task.WaitAsync(ct); }
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    sender.EnqueueTraceDetails(root, Enumerable.Range(0, 63).Select(_ => new SpanData()).ToList(), []);
    await firstDetail.Task.WaitAsync(TimeSpan.FromSeconds(5));
    for (var i = 0; i < 24; i++)
        sender.EnqueueTraceDetails(root, Enumerable.Range(0, 15).Select(_ => new SpanData()).ToList(), []);
    releaseDetail.TrySetResult();
    await Task.Delay(500);
    Check(detailedCount == 448, "full detail batches do not wait by trace envelope count");
}

var httpCaptured = new ConcurrentQueue<TelemetryBatch>();
await using (var agent = new LocalTelemetry(options, new CaptureHandler(async (request, ct) =>
{
    httpCaptured.Enqueue((await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json, ct))!);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
})))
{
    using var ambient = new Activity("uncollected.host").SetIdFormat(ActivityIdFormat.W3C).Start();
    ActivityContext cause;
    using (var httpEntry = agent.StartHttpRequest("POST", "/orders/{id}", Guid.NewGuid(), 7, sourceFile: "OrderHandler.cs"))
    {
        Check(httpEntry!.TraceId != ambient.TraceId && httpEntry.ParentSpanId == default, "HTTP entry does not inherit an uncollected host parent");
        cause = httpEntry.Context;
        httpEntry.SetTag("http.response.status_code", 409);
        using (LocalTelemetry.StartSpan("SQLite.lookup", "database")) { }
        LocalTelemetry.SetOutcome("rejected");
    }
    Check(Activity.Current == ambient, "HTTP entry restores host ambient activity");
    var remoteParent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
    using (var remote = agent.StartHttpRequest("GET", "/stock", Guid.NewGuid(), 7, remoteParent))
    {
        Check(remote!.TraceId == remoteParent.TraceId && remote.ParentSpanId == remoteParent.SpanId, "explicit HTTP parent preserves distributed trace");
        remote.SetTag("http.response.status_code", 200);
        LocalTelemetry.SetOutcome("success");
    }
    using (var job = agent.StartJob("Synthetic.Job", agent.UserKey(7), "fixture-session", cause))
    {
        Check(job!.TraceId != cause.TraceId && job.ParentSpanId == default, "asynchronous job starts a separate trace");
        LocalTelemetry.SetOutcome("success");
    }
    Check(Activity.Current == ambient, "job restores ambient activity");
}
var httpSpans = StoredSpans(httpCaptured);
Check(httpCaptured.SelectMany(b => b.Metrics).Any(m => m.Method == "POST" && m.Path == "/orders/{id}" && m.StatusCode == 409),
    "HTTP metrics retain method template and real status");
Check(httpSpans.Any(s => s.Name == "SQLite.lookup"), "SQLite spans are not mislabeled PostgreSQL");
var jobRoot = httpSpans.Single(s => s.Name == "Synthetic.Job");
Check(jobRoot.LinkedTraceId is { Length: 32 } && jobRoot.LinkedSpanId is { Length: 16 } && jobRoot.UserKey is not null,
    "job retains causal link and pseudonymous user");

if (args.Length > 0)
{
    var endpoint = new Uri(args[0]);
    await using (var agent = new LocalTelemetry(new AgentOptions
    {
        Endpoint = endpoint, ServiceName = "VerificationProbe", RunId = "synthetic-agent-checks",
        DetailSampleRate = 1,
        CodeVersion = "verification-fixture-v1", FlushInterval = TimeSpan.FromMilliseconds(30), ActiveSessions = () => 3
    }))
    {
        for (var i = 0; i < 3; i++)
        {
            using var activity = agent.StartRequest($"Synthetic.{new[] { "Normal", "Slow", "Failure" }[i]}", Guid.NewGuid(), 70001);
            using (LocalTelemetry.StartSpan("synthetic.database_wait", "database")) await Task.Delay(i == 1 ? 150 : 5);
            if (i == 2)
            {
                try { throw new InvalidOperationException("SECRET_TOKEN_DO_NOT_EXPORT"); }
                catch (Exception ex) { LocalTelemetry.RecordException(ex); }
            }
            else LocalTelemetry.SetOutcome("success", "fixture");
        }
        await Task.Delay(5500);
        Check(agent.Sender.Health.FailedBatches == 0 && agent.Sender.Health.Sent >= 6, "live collector accepts spans and metrics");
    }
    using var http = new HttpClient { BaseAddress = endpoint };
    var page = await http.GetFromJsonAsync<JsonElement>("/api/traces?server=VerificationProbe");
    Check(page.GetProperty("items").GetArrayLength() >= 3, "request explorer retrieves agent data");
    var resources = await http.GetFromJsonAsync<JsonElement>("/api/traces/runtime?server=VerificationProbe");
    Check(resources.GetArrayLength() > 0, "runtime samples reach collector");
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
