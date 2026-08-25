namespace CollectionServer.Dtos;

public record MetricIngestDto(
    DateTime Timestamp,
    string ServerName,
    string Method,
    string Path,
    int StatusCode,
    double DurationMs);

public record MetricSummaryDto(
    string Path,
    long Count,
    double AvgDurationMs,
    double P95DurationMs,
    double ErrorRate);

public record AnomalyDto(
    string ServerName,
    string Path,
    DateTime Timestamp,
    double ObservedDurationMs,
    double BaselineDurationMs,
    int StatusCode,
    string Reason);

public record AnalysisResponseDto(
    string ServerName,
    string Path,
    DateTime Timestamp,
    double ObservedDurationMs,
    double BaselineDurationMs,
    string Reason,
    string LlmAnalysis,
    bool IsAdvisoryOnly = true);
