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
}
