using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CollectionServer.Options;
using Microsoft.Extensions.Options;

namespace CollectionServer.Services;

public interface ILmStudioClient
{
    Task<string> AnalyzeAsync(string prompt, CancellationToken ct = default);
}

/// <summary>
/// LM Studio의 로컬 서버(OpenAI 호환 /v1/chat/completions)를 호출하는 클라이언트.
/// LM Studio 앱에서 "Local Server" 를 켜고 모델을 로드해 두어야 함 (기본 포트 1234).
/// </summary>
public class LmStudioClient(HttpClient httpClient, IOptions<LmStudioOptions> options, ILogger<LmStudioClient> logger)
    : ILmStudioClient
{
    private readonly LmStudioOptions _options = options.Value;

    public async Task<string> AnalyzeAsync(string prompt, CancellationToken ct = default)
    {
        var request = new ChatCompletionRequest
        {
            Model = _options.Model,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            Messages =
            [
                new ChatMessage("system",
                    "너는 APM(애플리케이션 성능 모니터링) 전문가야. 주어진 이상탐지 지표를 근거로 " +
                    "발생 가능한 원인을 한국어로 2~4문장으로 간결하게 설명하고, 실행 가능한 개선 방향을 1~2가지 제안해. " +
                    "추측일 경우 단정하지 말고 가능성으로 표현해. " +
                    "마크다운 문법(**, #, - 목록 기호 등)을 쓰지 말고 순수 텍스트 문장으로만 답해."),
                new ChatMessage("user", prompt)
            ]
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(content)
                ? "LM Studio로부터 빈 응답을 받았습니다."
                : content.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LM Studio 호출 실패");
            return $"LM Studio 호출 실패: {ex.Message} (LM Studio 앱에서 Local Server가 실행 중인지, 모델이 로드되어 있는지 확인하세요.)";
        }
    }

    private record ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; init; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;
    }

    private record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public List<ChatChoice>? Choices { get; init; }
    }

    private record ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
    }
}
