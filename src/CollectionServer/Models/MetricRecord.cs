namespace CollectionServer.Models;

public class MetricRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
}
