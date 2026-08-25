namespace TargetServer.Options;

public class MetricsOptions
{
    public const string SectionName = "Metrics";

    public string CollectionServerBaseUrl { get; set; } = "http://localhost:5219";
    public string ServerName { get; set; } = "TargetServer";
}
