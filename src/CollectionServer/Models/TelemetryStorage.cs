namespace CollectionServer.Models;

public sealed class TelemetryBatchReceipt
{
    public string Id { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}

public sealed class TraceInvestigation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TraceId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "trace-evidence-v1";
    public string EvidenceJson { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Status { get; set; } = "pending";
    public double DurationMs { get; set; }
}
