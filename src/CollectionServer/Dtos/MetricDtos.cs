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
    string? SuspectedCode = null,
    bool IsAdvisoryOnly = true,
    List<CodeSnippetDto>? SuspectedCodeSnippets = null);

// LLM이 "의심 코드 N: ..." 형식으로 지목한 후보 하나. 실제 소스 파일에서 라인을 못 찾으면
// Lines가 null(그래도 이유 텍스트는 번호와 함께 보여줌).
public record CodeSnippetDto(
    int Index,
    string Reason,
    string? FilePath,
    int? LineNumber,
    List<CodeSnippetLineDto>? Lines);

public record CodeSnippetLineDto(int LineNumber, string Text, bool IsSuspect);
