namespace CollectionServer.Options;

public class LmStudioOptions
{
    public const string SectionName = "LmStudio";

    public string BaseUrl { get; set; } = "http://localhost:1234";
    public string Model { get; set; } = "local-model";
    public int TimeoutSeconds { get; set; } = 60;
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 400;
}
