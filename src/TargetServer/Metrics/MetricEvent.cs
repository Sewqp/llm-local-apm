namespace TargetServer.Metrics;

// CollectionServer의 MetricIngestDto와 동일한 JSON 형태(camelCase 필드)를 가짐
public record MetricEvent(
    DateTime Timestamp,
    string ServerName,
    string Method,
    string Path,
    int StatusCode,
    double DurationMs);
