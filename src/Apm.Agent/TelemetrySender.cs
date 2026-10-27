using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace Apm.Agent;

public sealed class TelemetrySender : IAsyncDisposable
{
    private sealed record Envelope(List<SpanData> Spans, List<LogData> Logs, bool ProducesMetrics = false);
    private readonly Channel<Envelope> _requests, _details;
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly string _instanceId;
    private readonly CancellationTokenSource _stop = new(), _runtimeStop = new();
    private readonly Task _requestWorker, _detailWorker, _runtimeWorker;
    private long _requestDropped, _detailDropped, _runtimeDropped, _failedBatches, _sent, _requestsObserved, _requestsSent;
    private long _sampledOut, _limited, _uncorrelated;
    private int _detailQueued, _disposed;
    public AgentHealth Health => new(_requests.Reader.Count + Volatile.Read(ref _detailQueued),
        Interlocked.Read(ref _requestDropped) + Interlocked.Read(ref _detailDropped) + Interlocked.Read(ref _runtimeDropped),
        Interlocked.Read(ref _failedBatches), Interlocked.Read(ref _sent), _requests.Reader.Count,
        Interlocked.Read(ref _requestsObserved), Interlocked.Read(ref _requestsSent), Interlocked.Read(ref _requestDropped),
        Interlocked.Read(ref _detailDropped), Interlocked.Read(ref _sampledOut), Interlocked.Read(ref _limited), Interlocked.Read(ref _uncorrelated));

    public TelemetrySender(AgentOptions options, string instanceId, HttpMessageHandler? handler = null)
    {
        if (!options.Endpoint.IsLoopback || options.Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("The local collector must use a loopback HTTP(S) endpoint.");
        if (options.QueueCapacity < 1 || options.RequestQueueCapacity < 1 || options.BatchSize is < 1 or > 256 ||
            options.MaxAttempts is < 1 or > 3 || options.FlushInterval <= TimeSpan.Zero || options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid telemetry queue or timeout configuration.");
        _options = options; _instanceId = instanceId;
        _requests = CreateQueue(options.RequestQueueCapacity);
        _details = CreateQueue(options.QueueCapacity);
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = options.Endpoint, Timeout = options.RequestTimeout };
        _requestWorker = SendLoopAsync(_requests, true);
        _detailWorker = SendLoopAsync(_details, false);
        _runtimeWorker = RuntimeLoopAsync();
    }

    private static Channel<Envelope> CreateQueue(int capacity) => Channel.CreateBounded<Envelope>(
        new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public void RecordDrop() => Interlocked.Increment(ref _detailDropped);
    public void RecordSampledOut() => Interlocked.Increment(ref _sampledOut);
    public void RecordLimited() => Interlocked.Increment(ref _limited);
    public void RecordUncorrelated() => Interlocked.Increment(ref _uncorrelated);

    public bool Enqueue(SpanData span, List<LogData>? logs = null)
    {
        if (!span.IsEntry) return EnqueueDetails([span], logs ?? []);
        Interlocked.Increment(ref _requestsObserved);
        if (_requests.Writer.TryWrite(new Envelope([span], logs ?? [], true))) return true;
        Interlocked.Increment(ref _requestDropped);
        return false;
    }

    public void EnqueueTraceDetails(SpanData root, List<SpanData> children, List<LogData> childLogs)
    {
        if (EnqueueDetails([root, ..children], childLogs)) return;
        root.DetailStatus = "queue_dropped";
        if (!_requests.Writer.TryWrite(new Envelope([root], []))) Interlocked.Increment(ref _detailDropped);
    }

    private bool EnqueueDetails(List<SpanData> spans, List<LogData> logs)
    {
        // Reserve capacity for the entire trace fragment, not arbitrary individual children.
        if (Interlocked.Add(ref _detailQueued, spans.Count) <= _options.QueueCapacity &&
            _details.Writer.TryWrite(new Envelope(spans, logs))) return true;
        Interlocked.Add(ref _detailQueued, -spans.Count);
        Interlocked.Add(ref _detailDropped, spans.Count);
        return false;
    }

    private async Task SendLoopAsync(Channel<Envelope> queue, bool requests)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(_stop.Token))
            {
                // A detail envelope holds an entire trace fragment. Waiting by envelope count
                // throttles a full span backlog as if it were an almost-empty queue.
                var queuedSpans = requests ? queue.Reader.Count : Volatile.Read(ref _detailQueued);
                if (queuedSpans < _options.BatchSize) await Task.Delay(_options.FlushInterval, _stop.Token);
                var spans = new List<SpanData>(); var logs = new List<LogData>(); var metrics = new List<MetricData>();
                while (queue.Reader.TryPeek(out var next) && spans.Count + next.Spans.Count <= 256)
                {
                    if (spans.Count >= _options.BatchSize) break;
                    if (!queue.Reader.TryRead(out var entry)) break;
                    spans.AddRange(entry.Spans); logs.AddRange(entry.Logs);
                    if (entry.ProducesMetrics) metrics.AddRange(entry.Spans.Select(ToMetric));
                    if (!requests) Interlocked.Add(ref _detailQueued, -entry.Spans.Count);
                }
                var batch = new TelemetryBatch(Guid.NewGuid().ToString("N"), spans, metrics, logs, []);
                var delivered = false;
                try { delivered = await SendBatchAsync(batch, _stop.Token); }
                finally
                {
                    if (delivered)
                    {
                        Interlocked.Add(ref _sent, spans.Count);
                        if (requests) Interlocked.Add(ref _requestsSent, metrics.Count);
                    }
                    else if (requests)
                    {
                        Interlocked.Add(ref _requestDropped, metrics.Count);
                        Interlocked.Add(ref _detailDropped, spans.Count - metrics.Count);
                    }
                    else Interlocked.Add(ref _detailDropped, spans.Count);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Interlocked.Increment(ref _failedBatches); }
    }

    private async Task<bool> SendBatchAsync(TelemetryBatch batch, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.MaxAttempts; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync("/api/telemetry/batch", batch, ct);
                if (response.IsSuccessStatusCode) return true;
                Interlocked.Increment(ref _failedBatches);
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests) return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Interlocked.Increment(ref _failedBatches);
                if (ct.IsCancellationRequested) return false;
            }
            if (attempt + 1 < _options.MaxAttempts) await Task.Delay(100 * (attempt + 1), ct);
        }
        return false;
    }

    private static MetricData ToMetric(SpanData span) => new()
    {
        Timestamp = span.StartTime, ServerName = span.ServiceName,
        Method = span.Category == "background" ? "BACKGROUND" : span.RequestMethod ?? "PACKET",
        Path = span.Route ?? $"/{(span.Category == "background" ? "background" : "packet")}/{span.Name}",
        StatusCode = span.HttpStatusCode ?? (span.Outcome is "error" or "timeout" ? 500 : span.Outcome == "rejected" ? 400 : 200),
        DurationMs = span.DurationMs, TraceId = span.TraceId, RunId = span.RunId,
        Outcome = span.Outcome, UserKey = span.UserKey, SessionId = span.SessionId
    };

    private async Task RuntimeLoopAsync()
    {
        using var process = Process.GetCurrentProcess();
        var previousCpu = process.TotalProcessorTime; var previous = Stopwatch.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(_runtimeStop.Token))
            {
                process.Refresh(); var cpu = process.TotalProcessorTime;
                var elapsed = Stopwatch.GetElapsedTime(previous).TotalMilliseconds; var health = Health;
                var sample = new RuntimeData
                {
                    Timestamp = DateTime.UtcNow, ServiceName = _options.ServiceName, InstanceId = _instanceId, RunId = _options.RunId,
                    CpuPercent = Math.Clamp((cpu - previousCpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100, 0, 100),
                    WorkingSetMb = process.WorkingSet64 / 1048576d, GcHeapMb = GC.GetTotalMemory(false) / 1048576d,
                    ThreadPoolQueue = ThreadPool.PendingWorkItemCount, ThreadPoolThreads = ThreadPool.ThreadCount,
                    ActiveSessions = _options.ActiveSessions?.Invoke() ?? 0,
                    QueueDepth = health.QueueDepth, Dropped = health.Dropped, FailedBatches = health.FailedBatches, Sent = health.Sent,
                    RequestQueueDepth = health.RequestQueueDepth, RequestsObserved = health.RequestsObserved, RequestsSent = health.RequestsSent,
                    RequestsDropped = health.RequestsDropped, DetailsDropped = health.DetailsDropped,
                    SampledOutTraces = health.SampledOutTraces, LimitedTraces = health.LimitedTraces,
                    UncorrelatedSpansSkipped = health.UncorrelatedSpansSkipped, DetailSampleRate = _options.DetailSampleRate
                };
                // Health reports never compete with either bounded data queue.
                var batch = new TelemetryBatch(Guid.NewGuid().ToString("N"), [], [], [], [sample]);
                if (await SendBatchAsync(batch, _runtimeStop.Token)) Interlocked.Increment(ref _sent);
                else Interlocked.Increment(ref _runtimeDropped);
                previousCpu = cpu; previous = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Interlocked.Increment(ref _runtimeDropped); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _runtimeStop.CancelAsync(); await _runtimeWorker;
        _requests.Writer.TryComplete(); _details.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(5));
        await Task.WhenAll(_requestWorker, _detailWorker);
        while (_requests.Reader.TryRead(out var request))
        {
            if (request.ProducesMetrics) Interlocked.Add(ref _requestDropped, request.Spans.Count);
            else Interlocked.Add(ref _detailDropped, request.Spans.Count);
        }
        while (_details.Reader.TryRead(out var detail))
        { Interlocked.Add(ref _detailDropped, detail.Spans.Count); Interlocked.Add(ref _detailQueued, -detail.Spans.Count); }
        _http.Dispose(); _stop.Dispose(); _runtimeStop.Dispose();
    }
}
