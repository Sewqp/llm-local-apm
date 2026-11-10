using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    private readonly TelemetrySpool? _spool;
    private readonly CancellationTokenSource _replayStop = new();
    private readonly Task _replayWorker;
    private long _requestsSpooled, _replayedRequests, _spoolErrors;
    private readonly object _overflowGate = new();
    private long _requestDropped, _detailDropped, _runtimeDropped, _failedBatches, _sent, _requestsObserved, _requestsSent;
    private long _sampledOut, _limited, _uncorrelated;
    private int _detailQueued, _disposed;
    public AgentHealth Health => new(_requests.Reader.Count + Volatile.Read(ref _detailQueued),
        Interlocked.Read(ref _requestDropped) + Interlocked.Read(ref _detailDropped) + Interlocked.Read(ref _runtimeDropped),
        Interlocked.Read(ref _failedBatches), Interlocked.Read(ref _sent), _requests.Reader.Count,
        Interlocked.Read(ref _requestsObserved), Interlocked.Read(ref _requestsSent), Interlocked.Read(ref _requestDropped),
        Interlocked.Read(ref _detailDropped), Interlocked.Read(ref _sampledOut), Interlocked.Read(ref _limited), Interlocked.Read(ref _uncorrelated),
        _spool?.Health.Batches ?? 0, _spool?.Health.Requests ?? 0, _spool?.Health.Bytes ?? 0,
        Interlocked.Read(ref _requestsSpooled), Interlocked.Read(ref _replayedRequests), Interlocked.Read(ref _spoolErrors));

    public TelemetrySender(AgentOptions options, string instanceId, HttpMessageHandler? handler = null)
    {
        if (!options.Endpoint.IsLoopback || options.Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("The local collector must use a loopback HTTP(S) endpoint.");
        if (options.QueueCapacity < 1 || options.RequestQueueCapacity < 1 || options.BatchSize is < 1 or > 1024 ||
            options.MaxSpoolBytes < 1 || options.MaxSpoolBatches < 1 ||
            options.MaxAttempts is < 1 or > 3 || options.FlushInterval <= TimeSpan.Zero || options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid telemetry queue or timeout configuration.");
        _options = options; _instanceId = instanceId;
        _spool = options.SpoolDirectory is null ? null : new TelemetrySpool(options);
        _requests = CreateQueue(options.RequestQueueCapacity);
        _details = CreateQueue(options.QueueCapacity);
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = options.Endpoint, Timeout = options.RequestTimeout };
        _requestWorker = SendLoopAsync(_requests, true);
        _detailWorker = SendLoopAsync(_details, false);
        _runtimeWorker = RuntimeLoopAsync();
        _replayWorker = ReplayLoopAsync();
    }

    private static Channel<Envelope> CreateQueue(int capacity) => Channel.CreateBounded<Envelope>(
        new BoundedChannelOptions(capacity) { SingleReader = false, FullMode = BoundedChannelFullMode.Wait });

    public void RecordDrop() => Interlocked.Increment(ref _detailDropped);
    public void RecordSampledOut() => Interlocked.Increment(ref _sampledOut);
    public void RecordLimited() => Interlocked.Increment(ref _limited);
    public void RecordUncorrelated() => Interlocked.Increment(ref _uncorrelated);

    public bool Enqueue(SpanData span, List<LogData>? logs = null)
    {
        if (!span.IsEntry) return EnqueueDetails([span], logs ?? []);
        Interlocked.Increment(ref _requestsObserved);
        if (_requests.Writer.TryWrite(new Envelope([span], logs ?? [], true))) return true;
        if (_spool is not null) return SpillRequests(new Envelope([span], logs ?? [], true));
        Interlocked.Increment(ref _requestDropped);
        return false;
    }

    private bool SpillRequests(Envelope current)
    {
        lock (_overflowGate)
        {
            if (_requests.Writer.TryWrite(current)) return true;
            // Spill a full block of already observed requests, not one disk file per incoming packet.
            var entries = new List<Envelope> { current };
            while (entries.Count < 1024 && _requests.Reader.TryRead(out var pending)) entries.Add(pending);
            return PersistEntries(entries);
        }
    }

    private bool PersistEntries(List<Envelope> entries)
    {
        var batch = Batch(entries);
        if (entries.Count > 1 && (batch.Logs.Count > 1024 ||
            JsonSerializer.SerializeToUtf8Bytes(batch, TelemetrySpool.Json).Length > 2_000_000))
        {
            var middle = entries.Count / 2;
            var first = PersistEntries(entries.Take(middle).ToList());
            var second = PersistEntries(entries.Skip(middle).ToList());
            return first && second;
        }
        if (TrySpool(batch)) return true;
        RecordLost(batch);
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
        if (TrySpool(Batch([new Envelope(spans, logs)]))) return true;
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
                var entries = new List<Envelope>(); var spanCount = 0;
                while (queue.Reader.TryPeek(out var next) && spanCount + next.Spans.Count <= 1024)
                {
                    if (spanCount >= _options.BatchSize) break;
                    if (!queue.Reader.TryRead(out var entry)) break;
                    entries.Add(entry); spanCount += entry.Spans.Count;
                    if (!requests) Interlocked.Add(ref _detailQueued, -entry.Spans.Count);
                }
                if (entries.Count != 0) await DeliverAsync(entries);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Interlocked.Increment(ref _failedBatches); }
    }

    private static TelemetryBatch Batch(List<Envelope> entries) => new(Guid.NewGuid().ToString("N"),
        entries.SelectMany(e => e.Spans).ToList(), entries.Where(e => e.ProducesMetrics).SelectMany(e => e.Spans.Select(ToMetric)).ToList(),
        entries.SelectMany(e => e.Logs).ToList(), []);

    private async Task DeliverAsync(List<Envelope> entries)
    {
        var batch = Batch(entries);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, TelemetrySpool.Json);
        // Keep the existing HTTP byte/log limits even when the common small-request batch grows.
        if (bytes.Length > 2_000_000 || batch.Logs.Count > 1024)
        {
            if (entries.Count > 1)
            {
                var middle = entries.Count / 2;
                await DeliverAsync(entries.Take(middle).ToList());
                await DeliverAsync(entries.Skip(middle).ToList());
                return;
            }
            var entry = entries[0];
            if (entry.Spans.Count > 1)
            {
                foreach (var group in entry.Spans.Chunk((entry.Spans.Count + 1) / 2))
                {
                    var ids = group.Select(s => s.SpanId).ToHashSet();
                    await DeliverAsync([new Envelope(group.ToList(), entry.Logs.Where(l => ids.Contains(l.SpanId)).ToList(), entry.ProducesMetrics)]);
                }
                return;
            }
            if (entry.Logs.Count > 1)
            {
                var middle = entry.Logs.Count / 2;
                await DeliverAsync([entry with { Logs = entry.Logs.Take(middle).ToList() }]);
                await DeliverAsync([entry with { Logs = entry.Logs.Skip(middle).ToList(), ProducesMetrics = false }]);
                return;
            }
            RecordLost(batch); // One invalid, oversized record cannot be retried within the wire contract.
            return;
        }
        var delivered = false;
        try { delivered = await SendBatchAsync(batch, _stop.Token, bytes); }
        finally
        {
            if (delivered)
            {
                Interlocked.Add(ref _sent, batch.Spans.Count);
                Interlocked.Add(ref _requestsSent, batch.Metrics.Count);
            }
            else if (!TrySpool(batch)) RecordLost(batch);
        }
    }

    private bool TrySpool(TelemetryBatch batch)
    {
        if (_spool is null) return false;
        try
        {
            if (_spool.TryStore(batch))
            {
                Interlocked.Add(ref _requestsSpooled, batch.Metrics.Count);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Interlocked.Increment(ref _spoolErrors);
        return false;
    }

    private void RecordLost(TelemetryBatch batch)
    {
        Interlocked.Add(ref _requestDropped, batch.Metrics.Count);
        Interlocked.Add(ref _detailDropped, batch.Spans.Count - batch.Metrics.Count);
    }

    private async Task ReplayLoopAsync()
    {
        if (_spool is null) return;
        try
        {
            while (!_replayStop.IsCancellationRequested)
            {
                try
                {
                    if (_spool.Peek() is { } pending && await SendBatchAsync(pending.Batch, _replayStop.Token))
                    {
                        _spool.Acknowledge(pending.Path);
                        Interlocked.Add(ref _sent, pending.Batch.Spans.Count);
                        Interlocked.Add(ref _replayedRequests, pending.Batch.Metrics.Count);
                        if (pending.Batch.Spans.All(s => s.InstanceId == _instanceId))
                            Interlocked.Add(ref _requestsSent, pending.Batch.Metrics.Count);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { Interlocked.Increment(ref _spoolErrors); }
                await Task.Delay(500, _replayStop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> SendBatchAsync(TelemetryBatch batch, CancellationToken ct, byte[]? bytes = null)
    {
        bytes ??= JsonSerializer.SerializeToUtf8Bytes(batch, TelemetrySpool.Json);
        for (var attempt = 0; attempt < _options.MaxAttempts; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("application/json");
                using var response = await _http.PostAsync("/api/telemetry/batch", content, ct);
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
                    UncorrelatedSpansSkipped = health.UncorrelatedSpansSkipped, DetailSampleRate = _options.DetailSampleRate,
                    SpoolPendingBatches = health.SpoolPendingBatches, SpoolPendingRequests = health.SpoolPendingRequests,
                    SpoolBytes = health.SpoolBytes, RequestsSpooled = health.RequestsSpooled,
                    ReplayedRequests = health.ReplayedRequests, SpoolErrors = health.SpoolErrors
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
        await _replayStop.CancelAsync(); await _replayWorker;
        _requests.Writer.TryComplete(); _details.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(5));
        await Task.WhenAll(_requestWorker, _detailWorker);
        while (_requests.Reader.TryRead(out var request))
        {
            var batch = Batch([request]);
            if (!TrySpool(batch)) RecordLost(batch);
        }
        while (_details.Reader.TryRead(out var detail))
        {
            var batch = Batch([detail]);
            if (!TrySpool(batch)) RecordLost(batch);
            Interlocked.Add(ref _detailQueued, -detail.Spans.Count);
        }
        _spool?.Dispose(); _replayStop.Dispose();
        _http.Dispose(); _stop.Dispose(); _runtimeStop.Dispose();
    }
}
