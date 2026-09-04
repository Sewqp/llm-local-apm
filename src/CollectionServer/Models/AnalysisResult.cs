namespace CollectionServer.Models;

public class AnalysisResult
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string AnomalyReason { get; set; } = string.Empty;
    public double ObservedDurationMs { get; set; }
    public double BaselineDurationMs { get; set; }
    public string LlmAnalysis { get; set; } = string.Empty;
    public string? SuspectedCode { get; set; }

    // "빠른 분석"(quick) / "전체 분석"(full) — 어떤 모드로 나온 결과인지 이력에 남겨서 대시보드에 배지로 표시
    public string Mode { get; set; } = "quick";
}
