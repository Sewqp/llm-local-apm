namespace Apm.Agent;

public sealed class SpanData
{
    public string TraceId { get; set; } = "";
    public string SpanId { get; set; } = "";
    public string? ParentSpanId { get; set; }
    public string RunId { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime StartTime { get; set; }
    public double DurationMs { get; set; }
    public string Outcome { get; set; } = "unknown";
    public string? ResultCode { get; set; }
    public string? UserKey { get; set; }
    public string? SessionId { get; set; }
    public bool IsEntry { get; set; }
    public string? ErrorType { get; set; }
    public string CodeVersion { get; set; } = "";
    public string? DetailStatus { get; set; }
    public string? SamplingReason { get; set; }
    public string? RequestMethod { get; set; }
    public string? Route { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? SourceFile { get; set; }
    public string? LinkedTraceId { get; set; }
    public string? LinkedSpanId { get; set; }
    public int ExpectedDetailSpans { get; set; }
    public int LimitedDetailSpans { get; set; }
    internal SpanData Copy() => (SpanData)MemberwiseClone();
}

public sealed class MetricData
{
    public DateTime Timestamp { get; set; }
    public string ServerName { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public string? TraceId { get; set; }
    public string? RunId { get; set; }
    public string? Outcome { get; set; }
    public string? UserKey { get; set; }
    public string? SessionId { get; set; }
}

public sealed class LogData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; }
    public string TraceId { get; set; } = "";
    public string SpanId { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Level { get; set; } = "error";
    public string Message { get; set; } = "";
    public string? ErrorType { get; set; }
    public string? StackTrace { get; set; }
}

public sealed class RuntimeData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; }
    public string ServiceName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string RunId { get; set; } = "";
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public double GcHeapMb { get; set; }
    public long ThreadPoolQueue { get; set; }
    public int ThreadPoolThreads { get; set; }
    public int ActiveSessions { get; set; }
    public long QueueDepth { get; set; }
    public long Dropped { get; set; }
    public long FailedBatches { get; set; }
    public long Sent { get; set; }
    public long RequestQueueDepth { get; set; }
    public long RequestsObserved { get; set; }
    public long RequestsSent { get; set; }
    public long RequestsDropped { get; set; }
    public long DetailsDropped { get; set; }
    public long SampledOutTraces { get; set; }
    public long LimitedTraces { get; set; }
    public long UncorrelatedSpansSkipped { get; set; }
    public double DetailSampleRate { get; set; }
}

public sealed record TelemetryBatch(string BatchId, List<SpanData> Spans, List<MetricData> Metrics,
    List<LogData> Logs, List<RuntimeData> Runtime);

public sealed record AgentHealth(long QueueDepth, long Dropped, long FailedBatches, long Sent,
    long RequestQueueDepth, long RequestsObserved, long RequestsSent, long RequestsDropped,
    long DetailsDropped, long SampledOutTraces, long LimitedTraces, long UncorrelatedSpansSkipped);

public sealed class AgentOptions
{
    public string ServiceName { get; init; } = "GameServer";
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string CodeVersion { get; init; } = "working-tree";
    public Uri Endpoint { get; init; } = new("http://127.0.0.1:5219");
    // A bounded burst reserve for selected slow-login traces; independent of request statistics.
    public int QueueCapacity { get; init; } = 32768;
    public int RequestQueueCapacity { get; init; } = 32768;
    public int BatchSize { get; init; } = 256;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxAttempts { get; init; } = 2;
    public Func<int>? ActiveSessions { get; init; }
    public byte[]? UserHashKey { get; init; }
    public bool CaptureDependencies { get; init; } = true;
    public double DetailSampleRate { get; init; } = 0.01;
    public double SlowTraceMs { get; init; } = 100;
    public int MaxBufferedTraces { get; init; } = 4096;
    public int MaxDetailSpansPerTrace { get; init; } = 64;
    public TimeSpan DetailFlushDelay { get; init; } = TimeSpan.FromSeconds(1);
}
