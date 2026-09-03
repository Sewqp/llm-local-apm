using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionServer.Options;
using Microsoft.Extensions.Options;

namespace CollectionServer.Services;

public interface ILmStudioClient
{
    /// <summary>
    /// serverName을 주면 "정밀탐색"(tool-calling) 모드로 동작 — LLM이 직접 graphify_explain/
    /// graphify_query 도구를 호출해가며 코드를 훑어본 뒤 결론을 낸다. serverName이 없거나 해당
    /// 서버에 Graphify 소스 디렉터리가 등록 안 돼 있으면 도구 없이 일반 응답만 받는다.
    /// </summary>
    Task<string> AnalyzeAsync(string prompt, string? serverName = null, CancellationToken ct = default);
}

/// <summary>
/// LM Studio의 로컬 서버(OpenAI 호환 /v1/chat/completions)를 호출하는 클라이언트.
/// LM Studio 앱에서 "Local Server" 를 켜고 모델을 로드해 두어야 함 (기본 포트 1234).
/// </summary>
public class LmStudioClient(
    HttpClient httpClient, IOptions<LmStudioOptions> options, IGraphifyService graphify, ILogger<LmStudioClient> logger)
    : ILmStudioClient
{
    private readonly LmStudioOptions _options = options.Value;

    // 도구 호출을 무한히 허용하면 모델이 계속 캐고 들어가거나(느린 로컬 모델이라 체감 지연 큼),
    // 이상한 이름으로 반복 호출하며 맴돌 수 있어서 라운드를 제한 — 다 쓰면 그때까지 본 정보로 결론 강제.
    private const int MaxToolRounds = 4;

    public async Task<string> AnalyzeAsync(string prompt, string? serverName = null, CancellationToken ct = default)
    {
        var tools = serverName is not null && graphify.GetSourceDir(serverName) is not null ? BuildTools() : null;

        var messages = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(tools is not null)),
            new("user", prompt),
        };

        for (int round = 0; ; round++)
        {
            var forceNoTools = round >= MaxToolRounds;
            var request = new ChatCompletionRequest
            {
                Model = _options.Model,
                Temperature = _options.Temperature,
                MaxTokens = _options.MaxTokens,
                Messages = messages,
                Tools = forceNoTools ? null : tools,
            };

            ChatMessage? message;
            try
            {
                using var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
                message = body?.Choices?.FirstOrDefault()?.Message;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LM Studio 호출 실패");
                return $"LM Studio 호출 실패: {ex.Message} (LM Studio 앱에서 Local Server가 실행 중인지, 모델이 로드되어 있는지 확인하세요.)";
            }

            if (message is null) return "LM Studio로부터 빈 응답을 받았습니다.";

            // 모델이 형식을 안 지켜서 tool_calls 없이 그냥 텍스트로만 "OOO를 찾아봐야겠다" 식으로
            // 답하는 로컬 소형 모델 특성도 있어서, tool_calls가 비어있으면 그걸 최종 답으로 받아들인다.
            if (message.ToolCalls is not { Count: > 0 } toolCalls || forceNoTools)
                return string.IsNullOrWhiteSpace(message.Content) ? "LM Studio로부터 빈 응답을 받았습니다." : message.Content.Trim();

            messages.Add(message);
            foreach (var call in toolCalls)
                messages.Add(await ExecuteToolAsync(call, serverName!, ct));
        }
    }

    private async Task<ChatMessage> ExecuteToolAsync(ToolCall call, string serverName, CancellationToken ct)
    {
        string result;
        try
        {
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
            result = call.Function.Name switch
            {
                "graphify_explain" when args.RootElement.TryGetProperty("name", out var n) =>
                    await graphify.ExplainAsync(serverName, n.GetString() ?? "", ct) ?? "결과 없음 — 그래프에서 이 이름을 찾지 못함. 다른 이름으로 다시 시도하거나 graphify_query로 넓게 검색해봐.",
                "graphify_query" when args.RootElement.TryGetProperty("term", out var t) =>
                    await graphify.QueryAsync(serverName, t.GetString() ?? "", ct) ?? "결과 없음 — 이 검색어로 그래프에서 아무것도 못 찾음.",
                _ => $"알 수 없는 도구 호출: {call.Function.Name}",
            };
        }
        catch (Exception ex)
        {
            result = $"도구 실행 실패: {ex.Message}";
        }

        return new ChatMessage("tool", result) { ToolCallId = call.Id };
    }

    private static string BuildSystemPrompt(bool toolsEnabled) =>
        "너는 APM(애플리케이션 성능 모니터링) 전문가야. 주어진 이상탐지 지표를 근거로 " +
        "발생 가능한 원인을 한국어로 2~4문장으로 간결하게 설명하고, 실행 가능한 개선 방향을 1~2가지 제안해. " +
        "추측일 경우 단정하지 말고 가능성으로 표현해. " +
        "마크다운 문법(**, #, - 목록 기호 등)을 쓰지 말고 순수 텍스트 문장으로만 답해. " +
        "사용자 메시지에 마지막에 형식 지시가 있으면 그 형식을 정확히 그대로 지켜서 답해." +
        (toolsEnabled
            ? " graphify_explain/graphify_query 도구로 실제 코드베이스를 직접 훑어볼 수 있어 — " +
              "이상탐지된 엔드포인트 하나에만 시야를 좁히지 말고, 같은 시간대에 다른 엔드포인트도 같이 " +
              "이상탐지됐다면 백그라운드 서비스나 공유 인프라처럼 여러 엔드포인트에 공통으로 영향을 줄 만한 " +
              "코드도 이름을 추측해서 찾아봐(예: XxxManager, XxxService 같은 이름). 확신이 설 때까지 " +
              "도구를 반복 호출해도 되지만, 결론이 서면 더 호출하지 말고 바로 답해."
            : "");

    private static List<ToolDefinition> BuildTools() =>
    [
        new ToolDefinition("function", new ToolFunctionDefinition(
            "graphify_explain",
            "코드베이스 지식그래프에서 정확한 이름의 클래스/함수/파일 노드를 찾아 소스 위치와 연결관계를 반환한다. 이름을 정확히 알 때 사용.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "찾을 클래스/함수/파일 이름 (예: HeartbeatManager)" },
                },
                ["required"] = new[] { "name" },
            })),
        new ToolDefinition("function", new ToolFunctionDefinition(
            "graphify_query",
            "코드베이스 지식그래프에서 검색어와 가장 가까운 노드부터 넓게 탐색한다. 정확한 이름을 모를 때 사용.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["term"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "검색어" },
                },
                ["required"] = new[] { "term" },
            })),
    ];

    private sealed record ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; init; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;
        [JsonPropertyName("tools")] public List<ToolDefinition>? Tools { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

        public ChatMessage() { }
        public ChatMessage(string role, string? content) { Role = role; Content = content; }
    }

    private sealed record ToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ToolCallFunction Function);

    private sealed record ToolCallFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);

    private sealed record ToolDefinition(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ToolFunctionDefinition Function);

    private sealed record ToolFunctionDefinition(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] object Parameters);

    private sealed record ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public List<ChatChoice>? Choices { get; init; }
    }

    private sealed record ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
    }
}
