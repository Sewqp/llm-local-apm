using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Apm.Agent;

public sealed class LocalTelemetry : IAsyncDisposable
{
    public const string SourceName = "LocalApm.GameServer";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
    private readonly AgentOptions _options;
    private readonly ActivityListener _listener;
    private readonly byte[] _hashKey;
    private readonly ConcurrentDictionary<ActivityTraceId, TraceBuffer> _traces = new();
    private int _bufferCount;
    private readonly CancellationTokenSource _flushStop = new();
    private readonly Task _flushWorker;
    public TelemetrySender Sender { get; }
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public LocalTelemetry(AgentOptions options, HttpMessageHandler? handler = null)
    {
        if (options.UserHashKey is { Length: < 32 })
            throw new ArgumentException("APM_USER_HASH_KEY must contain at least 32 bytes when configured.");
        if (!double.IsFinite(options.DetailSampleRate) || options.DetailSampleRate is < 0 or > 1 ||
            !double.IsFinite(options.SlowTraceMs) || options.SlowTraceMs < 0 ||
            options.MaxBufferedTraces < 1 || options.MaxDetailSpansPerTrace is < 1 or > 128 ||
            options.DetailFlushDelay < TimeSpan.Zero || options.DetailFlushDelay > TimeSpan.FromSeconds(5))
            throw new ArgumentException("Invalid trace capture limits.");
        _options = options;
        _hashKey = options.UserHashKey is { Length: >= 32 } key ? key.ToArray() : RandomNumberGenerator.GetBytes(32);
        Sender = new TelemetrySender(options, InstanceId, handler);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName || options.CaptureDependencies &&
                (source.Name.StartsWith("Npgsql") || source.Name.StartsWith("Microsoft.Orleans") ||
                 source.Name.StartsWith("OpenTelemetry.Instrumentation.StackExchangeRedis")),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = BeginTrace,
            ActivityStopped = Capture
        };
        ActivitySource.AddActivityListener(_listener);
        _flushWorker = FlushLoopAsync();
    }

    public string? UserKey(long playerId) => playerId == 0 ? null :
        Convert.ToHexStringLower(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(playerId.ToString())))[..24];

    public Activity? StartRequest(string name, Guid sessionId, long playerId)
    {
        var activity = Source.StartActivity(name, ActivityKind.Server, default(ActivityContext));
        activity?.SetTag("apm.entry", true);
        activity?.SetTag("apm.category", "packet");
        activity?.SetTag("apm.session", sessionId.ToString("N"));
        activity?.SetTag("apm.user", UserKey(playerId));
        activity?.SetTag("apm.outcome", "unknown");
        return activity;
    }

    public static Activity? StartSpan(string name, string category = "internal")
    {
        var activity = Source.StartActivity(name, category == "background" ? ActivityKind.Server : ActivityKind.Internal);
        activity?.SetTag("apm.category", category);
        if (category == "background") activity?.SetTag("apm.entry", true);
        return activity;
    }

    public Activity? StartHttpRequest(string method, string route, Guid sessionId, long userId,
        ActivityContext parent = default, string? sourceFile = null)
    {
        var activity = StartEntry($"{method} {route}", "http", ActivityKind.Server, parent);
        activity?.SetTag("apm.method", method);
        activity?.SetTag("apm.route", route);
        activity?.SetTag("apm.session", sessionId.ToString("N"));
        activity?.SetTag("apm.user", UserKey(userId));
        activity?.SetTag("apm.source", sourceFile);
        return activity;
    }

    public Activity? StartJob(string name, string? userKey, string? sessionId, ActivityContext cause,
        string? sourceFile = null)
    {
        // A queued job outlives its HTTP request. Keep a causal link instead of a late child.
        var activity = StartEntry(name, "background", ActivityKind.Consumer, default);
        activity?.SetTag("apm.user", userKey);
        activity?.SetTag("apm.session", sessionId);
        activity?.SetTag("apm.source", sourceFile);
        if (cause.TraceId != default && cause.SpanId != default)
        {
            activity?.SetTag("apm.link.trace", cause.TraceId.ToHexString());
            activity?.SetTag("apm.link.span", cause.SpanId.ToHexString());
        }
        return activity;
    }

    private static Activity? StartEntry(string name, string category, ActivityKind kind, ActivityContext parent)
    {
        // Empty explicit parent prevents an uncollected ASP.NET Activity becoming a hidden parent.
        // Activity.Start/Stop still restore the ambient Activity for the host's other instrumentation.
        var activity = parent == default
            ? Source.CreateActivity(name, kind, parentId: "", idFormat: ActivityIdFormat.W3C)?.Start()
            : Source.StartActivity(name, kind, parent);
        activity?.SetTag("apm.entry", true);
        activity?.SetTag("apm.category", category);
        activity?.SetTag("apm.outcome", "unknown");
        return activity;
    }

    public static void SetOutcome(string outcome, string? code = null)
    {
        for (var current = Activity.Current; current is not null; current = current.Parent)
        {
            if (current.GetTagItem("apm.entry") is not true) continue;
            current.SetTag("apm.outcome", outcome);
            current.SetTag("apm.result", code);
            if (outcome is "error" or "timeout") current.SetStatus(ActivityStatusCode.Error);
            return;
        }
    }

    public static void RecordException(Exception exception)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag("apm.error", type);
        // Exception messages and file paths can contain tokens, SQL parameters or account names.
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = type,
            ["exception.stacktrace"] = Limit(new StackTrace(exception, false).ToString(), 4000)
        }));
        SetOutcome(exception is TimeoutException ? "timeout" : exception is OperationCanceledException ? "cancelled" : "error", type);
    }

    private sealed class TraceBuffer
    {
        public List<SpanData> Spans { get; } = [];
        public List<LogData> Logs { get; } = [];
        public int Seen;
        public bool HasError, Sealed;
        public SpanData? Root;
        public long CompletedAt;
    }

    private void BeginTrace(Activity activity)
    {
        if (activity.Source.Name != SourceName || activity.Kind is not (ActivityKind.Server or ActivityKind.Consumer)) return;
        if (Interlocked.Increment(ref _bufferCount) <= _options.MaxBufferedTraces &&
            _traces.TryAdd(activity.TraceId, new TraceBuffer())) return;
        Interlocked.Decrement(ref _bufferCount);
    }

    private void Capture(Activity activity)
    {
        try
        {
            if (activity.GetTagItem("apm.entry") is true) { FinishTrace(activity); return; }
            if (!_traces.TryGetValue(activity.TraceId, out var buffer)) { Sender.RecordUncorrelated(); return; }
            lock (buffer)
            {
                if (buffer.Sealed) { Sender.RecordUncorrelated(); return; }
                buffer.Seen++;
                buffer.HasError |= activity.Status == ActivityStatusCode.Error;
                if (buffer.Spans.Count >= _options.MaxDetailSpansPerTrace) return;
                var span = ToSpan(activity);
                buffer.Spans.Add(span);
                buffer.Logs.AddRange(ToLogs(activity, span));
            }
        }
        catch { Sender.RecordDrop(); }
    }

    private void FinishTrace(Activity activity)
    {
        var root = ToSpan(activity);
        var logs = ToLogs(activity, root);
        if (!_traces.TryGetValue(activity.TraceId, out var buffer))
        {
            root.DetailStatus = "buffer_limit";
            Sender.RecordLimited(); Sender.Enqueue(root, logs); return;
        }
        lock (buffer)
        {
            root.SamplingReason = root.Outcome is "error" or "timeout" || buffer.HasError ? "error" :
                root.DurationMs >= _options.SlowTraceMs ? "slow" :
                Convert.ToUInt32(root.TraceId[..8], 16) / ((double)uint.MaxValue + 1) < _options.DetailSampleRate ? "sample" : "none";
            if (root.SamplingReason == "none")
            {
                buffer.Sealed = true;
                if (_traces.TryRemove(activity.TraceId, out _)) Interlocked.Decrement(ref _bufferCount);
                root.DetailStatus = "sampled_out";
                Sender.RecordSampledOut(); Sender.Enqueue(root, logs); return;
            }
            root.DetailStatus = "pending";
            buffer.Root = root;
            buffer.CompletedAt = Stopwatch.GetTimestamp();
            Sender.Enqueue(root.Copy(), logs);
        }
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_flushStop.Token)) FlushReady(false);
        }
        catch (OperationCanceledException) { }
    }

    private void FlushReady(bool force)
    {
        foreach (var (traceId, buffer) in _traces)
        {
            lock (buffer)
            {
                if (buffer.Root is not { } root || buffer.Sealed ||
                    !force && Stopwatch.GetElapsedTime(buffer.CompletedAt) < _options.DetailFlushDelay) continue;
                buffer.Sealed = true;
                if (_traces.TryRemove(traceId, out _)) Interlocked.Decrement(ref _bufferCount);
                root.ExpectedDetailSpans = buffer.Spans.Count;
                root.LimitedDetailSpans = buffer.Seen - buffer.Spans.Count;
                root.DetailStatus = root.LimitedDetailSpans > 0 ? "truncated" : "retained";
                if (buffer.HasError) root.SamplingReason = "error";
                if (root.LimitedDetailSpans > 0) Sender.RecordLimited();
                foreach (var child in buffer.Spans)
                {
                    child.UserKey ??= root.UserKey;
                    child.SessionId ??= root.SessionId;
                }
                Sender.EnqueueTraceDetails(root, buffer.Spans, buffer.Logs);
            }
        }
    }

    private SpanData ToSpan(Activity activity)
    {
        var category = activity.Source.Name.StartsWith("Npgsql") ? "database" :
                activity.Source.Name.Contains("Redis") ? "redis" :
                activity.Source.Name.StartsWith("Microsoft.Orleans") ? "grain" :
                activity.GetTagItem("apm.category")?.ToString() ?? "internal";
        return new SpanData
            {
                TraceId = activity.TraceId.ToHexString(), SpanId = activity.SpanId.ToHexString(),
                ParentSpanId = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
                RunId = _options.RunId, ServiceName = _options.ServiceName, InstanceId = InstanceId,
                Name = Limit(activity.Source.Name.StartsWith("Npgsql") ? "PostgreSQL" : category == "redis" ? "Redis" : activity.OperationName, 160) ?? "operation",
                Category = category, StartTime = activity.StartTimeUtc, DurationMs = activity.Duration.TotalMilliseconds,
                Outcome = activity.GetTagItem("apm.outcome")?.ToString() ?? (activity.Status == ActivityStatusCode.Error ? "error" : "unknown"),
                ResultCode = Limit(activity.GetTagItem("apm.result")?.ToString(), 160),
                UserKey = FindTag(activity, "apm.user"), SessionId = FindTag(activity, "apm.session"),
                IsEntry = activity.GetTagItem("apm.entry") is true,
                ErrorType = Limit(activity.GetTagItem("apm.error")?.ToString(), 160), CodeVersion = _options.CodeVersion,
                RequestMethod = Limit(activity.GetTagItem("apm.method")?.ToString(), 20),
                Route = Limit(activity.GetTagItem("apm.route")?.ToString(), 250),
                HttpStatusCode = activity.GetTagItem("http.response.status_code") is int status ? status : null,
                SourceFile = Limit(activity.GetTagItem("apm.source")?.ToString(), 250),
                LinkedTraceId = activity.GetTagItem("apm.link.trace")?.ToString(),
                LinkedSpanId = activity.GetTagItem("apm.link.span")?.ToString()
            };
    }

    private static List<LogData> ToLogs(Activity activity, SpanData span) =>
        activity.Events.Where(e => e.Name == "exception").Take(4).Select(e => new LogData
            {
                Timestamp = e.Timestamp.UtcDateTime, TraceId = span.TraceId, SpanId = span.SpanId,
                ServiceName = span.ServiceName, Message = "처리 중 예외가 발생했습니다. 원문 메시지는 수집하지 않습니다.",
                ErrorType = Limit(e.Tags.FirstOrDefault(t => t.Key == "exception.type").Value?.ToString(), 160),
                StackTrace = activity.Source.Name == SourceName ? Limit(e.Tags.FirstOrDefault(t => t.Key == "exception.stacktrace").Value?.ToString(), 4000) : null
        }).ToList();

    private static string? FindTag(Activity activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.GetTagItem(key) is string value) return value;
        return null;
    }

    private static string? Limit(string? value, int length) => value is null ? null : value[..Math.Min(value.Length, length)];

    public async ValueTask DisposeAsync()
    {
        // Redis profiling emits Activities after their parent has stopped.
        await Task.Delay(_options.DetailFlushDelay);
        _listener.Dispose();
        await _flushStop.CancelAsync();
        await _flushWorker;
        FlushReady(true);
        _traces.Clear();
        await Sender.DisposeAsync();
        _flushStop.Dispose();
        CryptographicOperations.ZeroMemory(_hashKey);
    }
}
