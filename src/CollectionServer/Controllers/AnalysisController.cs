using System.Text.RegularExpressions;
using CollectionServer.Data;
using CollectionServer.Dtos;
using CollectionServer.Models;
using CollectionServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Controllers;

[ApiController]
[Route("api/")]
public partial class AnalysisController(
    ApmDbContext db,
    IAnomalyDetectionService anomalyDetection,
    ILmStudioClient lmStudio,
    IGraphifyService graphify) : ControllerBase
{
    private const int MaxLlmCallsPerRun = 5;
    private const int MaxSuspectCandidates = 3;
    private const int SnippetContextLines = 5; // 의심 라인 앞뒤로 보여줄 줄 수

    // "의심 코드 N: ..." 형식의 줄을 전부 뽑아낸다(N번째 후보, 후보마다 별도 코드 스니펫으로 보여주기 위함).
    [GeneratedRegex(@"^\s*의심\s*코드\s*\d+\s*[:：]\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SuspectedCodeRegex();

    // 후보 한 줄 텍스트에서 "무슨 파일의 몇 번째 줄"을 최대한 관대하게 뽑아낸다. Graphify CLI의 출력
    // 자체가 "file.cs L12"(Source 줄)와 "file.cs:L17"(Connections 줄)처럼 형식이 섞여 있고, 정밀탐색
    // 모드에서는 LLM이 자기 말로 "file.cs loc=L16"처럼 더 자유롭게 풀어쓰기도 함(실측 확인됨) — 그래서
    // ".cs" 다음에 오는 구분자 길이를 넉넉히 잡고, 그 뒤 나오는 첫 숫자를 줄 번호로 본다. 다음 줄까지
    // 넘어가서 엉뚱한 숫자를 줍지 않도록 개행은 명시적으로 제외.
    [GeneratedRegex(@"([\w./\\-]+\.cs)[^\d\n]{0,20}(\d+)")]
    private static partial Regex FileLineRegex();

    // 이상탐지 결과만 빠르게 조회 (LLM 호출 없음)
    [HttpGet("anomalies")]
    public async Task<IActionResult> GetAnomalies(
        [FromQuery] int minutes = 30, [FromQuery] string? server = null, CancellationToken ct = default)
    {
        var anomalies = await anomalyDetection.DetectAsync(TimeSpan.FromMinutes(minutes), server, ct);
        return Ok(anomalies);
    }

    // 이상탐지 + LM Studio 원인분석/추천 생성 (최근 이상탐지 결과 중 경로별 최신 1건씩, 최대 MaxLlmCallsPerRun건)
    // mode=quick(기본, 백신의 "빠른 검사"에 대응)은 co-occurrence 신호가 있을 때만 배경 서비스 쪽을
    // 넌지시 유도하고, mode=full("전체 검사")은 신호가 없어도 배경 서비스를 강제로 넓게 훑게 한다.
    [HttpPost("analysis/run")]
    public async Task<IActionResult> RunAnalysis(
        [FromQuery] int minutes = 30, [FromQuery] string? server = null,
        [FromQuery] string mode = "quick", CancellationToken ct = default)
    {
        var thorough = mode == "full";
        var anomalies = await anomalyDetection.DetectAsync(TimeSpan.FromMinutes(minutes), server, ct);

        var targets = anomalies
            .GroupBy(a => a.Path)
            .Select(g => g.OrderByDescending(a => a.Timestamp).First())
            .OrderByDescending(a => a.ObservedDurationMs)
            .Take(MaxLlmCallsPerRun)
            .ToList();

        var responses = new List<AnalysisResponseDto>();

        foreach (var a in targets)
        {
            // 같은 실행(run)에서 다른 엔드포인트도 같이 이상탐지됐으면 알려줌 — 여러 엔드포인트가
            // 동시에 느려졌다면 각 핸들러 코드 자체보다 공유 인프라(백그라운드 서비스 등)가 원인일
            // 가능성이 높다는 신호라, LLM이 정밀탐색으로 그쪽도 찾아보게 유도하기 위함.
            var others = targets.Where(t => t.Path != a.Path).Select(t => t.Path).Distinct().ToList();
            var coOccurrenceSection = others.Count == 0
                ? ""
                : $"""

                   [참고] 같은 탐지 구간에 다음 엔드포인트도 함께 이상탐지됨: {string.Join(", ", others)}
                   여러 엔드포인트가 동시에 느려졌다면 각 핸들러 코드 자체의 문제보다 여러 요청이 공통으로
                   거치는 백그라운드 서비스나 자원(커넥션 풀, 주기적으로 도는 관리자 클래스 등)이 원인일
                   수 있으니, 정밀탐색 도구로 그런 이름도 추측해서 찾아봐(예: XxxManager, XxxService).
                   """;

            var toolsAvailable = graphify.GetSourceDir(a.ServerName) is not null;
            var suspectSection = !toolsAvailable
                ? ""
                : $"""

                   [의심 코드 보고 형식]
                   정밀탐색 도구(graphify_explain/graphify_query)로 코드를 실제로 확인해본 뒤, 이번
                   이상탐지의 원인일 가능성이 있는 파일/함수를 최대 {MaxSuspectCandidates}개, 가능성이
                   높은 순서로 각각 새 줄에 "의심 코드 N: <파일 경로와 줄 번호> - <한 줄 이유>" 형식으로
                   적어줘(N은 1부터). 확인해봐도 하나도 특정하기 어려우면 "의심 코드 1: 알 수 없음"이라고만 써줘.
                   """;

            var prompt =
                $"""
                 [이상탐지 지표]
                 - 서버: {a.ServerName}
                 - 엔드포인트: {a.Path}
                 - 발생 시각(UTC): {a.Timestamp:yyyy-MM-dd HH:mm:ss}
                 - HTTP 상태코드: {a.StatusCode}
                 - 관측 응답시간: {a.ObservedDurationMs:F0} ms
                 - 기준선 평균 응답시간: {a.BaselineDurationMs:F0} ms
                 - 탐지 사유: {a.Reason}
                 {coOccurrenceSection}
                 {suspectSection}
                 위 지표를 바탕으로 가능한 원인과 개선 방향을 설명해줘.
                 """;

            var analysisText = await lmStudio.AnalyzeAsync(prompt, a.ServerName, thorough, ct);

            var candidateLines = SuspectedCodeRegex().Matches(analysisText)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(line => !line.Contains("알 수 없음"))
                .Take(MaxSuspectCandidates)
                .ToList();
            analysisText = SuspectedCodeRegex().Replace(analysisText, "").TrimEnd();

            string? suspectedCode = candidateLines.Count > 0 ? string.Join('\n', candidateLines) : null;
            var snippets = BuildSnippets(a.ServerName, candidateLines);

            var result = new AnalysisResult
            {
                CreatedAt = DateTime.UtcNow,
                ServerName = a.ServerName,
                Path = a.Path,
                AnomalyReason = a.Reason,
                ObservedDurationMs = a.ObservedDurationMs,
                BaselineDurationMs = a.BaselineDurationMs,
                LlmAnalysis = analysisText,
                SuspectedCode = suspectedCode,
                Mode = mode
            };
            db.Analyses.Add(result);

            responses.Add(new AnalysisResponseDto(
                a.ServerName, a.Path, a.Timestamp, a.ObservedDurationMs, a.BaselineDurationMs,
                a.Reason, analysisText, suspectedCode, SuspectedCodeSnippets: snippets, Mode: mode));
        }

        if (responses.Count > 0)
            await db.SaveChangesAsync(ct);

        return Ok(responses);
    }

    // 과거에 생성된 분석 결과 이력 조회 (대시보드 새로고침 시 재호출 없이 표시용)
    [HttpGet("analysis")]
    public async Task<IActionResult> GetAnalysisHistory(
        [FromQuery] int limit = 20, [FromQuery] string? server = null, CancellationToken ct = default)
    {
        var scoped = db.Analyses.AsQueryable();
        if (!string.IsNullOrWhiteSpace(server))
            scoped = scoped.Where(a => a.ServerName == server);

        var rows = await scoped
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        // 스니펫은 저장해두지 않고 매번 실제 소스에서 다시 읽음(코드가 바뀌었을 수도 있고, 애초에
        // SuspectedCode 텍스트 자체가 원본 그대로라 재구성 비용도 파일 몇 개 읽는 정도로 작음).
        var items = rows.Select(a => new AnalysisResponseDto(
            a.ServerName, a.Path, a.CreatedAt, a.ObservedDurationMs, a.BaselineDurationMs,
            a.AnomalyReason, a.LlmAnalysis, a.SuspectedCode,
            SuspectedCodeSnippets: BuildSnippets(a.ServerName, SplitStoredCandidates(a.SuspectedCode)),
            Mode: a.Mode))
            .ToList();

        return Ok(items);
    }

    private static List<string> SplitStoredCandidates(string? suspectedCode) =>
        string.IsNullOrWhiteSpace(suspectedCode)
            ? []
            : suspectedCode.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // 후보 텍스트 목록을 번호를 매겨 코드 스니펫으로 바꾼다. 파일:라인을 못 찾아도(모델이 형식을 못
    // 지켰거나 그래프에 없는 이름을 지어낸 경우) 이유 텍스트만이라도 번호와 함께 보여준다 — 로컬 소형
    // LLM이 형식을 완벽히 안 지킬 수 있다는 걸 감안한 방어적 설계.
    private List<CodeSnippetDto> BuildSnippets(string serverName, List<string> candidateLines)
    {
        var sourceDir = graphify.GetSourceDir(serverName);
        var snippets = new List<CodeSnippetDto>();

        for (int i = 0; i < candidateLines.Count; i++)
        {
            var line = candidateLines[i];
            var fileMatch = FileLineRegex().Match(line);

            if (!fileMatch.Success || sourceDir is null)
            {
                snippets.Add(new CodeSnippetDto(i + 1, line, null, null, null));
                continue;
            }

            var rawFilePath = fileMatch.Groups[1].Value;
            var lineNumber = int.Parse(fileMatch.Groups[2].Value);
            var resolvedPath = ResolveSourceFile(sourceDir, rawFilePath);

            snippets.Add(new CodeSnippetDto(i + 1, line, rawFilePath, lineNumber,
                resolvedPath is null ? null : ReadSnippet(resolvedPath, lineNumber)));
        }

        return snippets;
    }

    // LLM이 준 경로가 sourceDir 기준 상대경로 그대로일 수도, 파일명만 남긴 것일 수도 있어서(Graphify CLI
    // 출력 자체가 "Packet/Handler/DebugHandler.cs"처럼 상대경로를 주지만 모델이 파일명만 베낄 수 있음)
    // 먼저 그대로 결합해보고, 없으면 파일명만으로 소스 트리 전체를 뒤진다.
    private static string? ResolveSourceFile(string sourceDir, string rawFilePath)
    {
        var normalized = rawFilePath.Replace('\\', '/');
        var direct = Path.Combine(sourceDir, normalized);
        if (System.IO.File.Exists(direct)) return direct;

        var fileName = Path.GetFileName(normalized);
        return Directory.EnumerateFiles(sourceDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private List<CodeSnippetLineDto>? ReadSnippet(string filePath, int lineNumber)
    {
        try
        {
            var allLines = System.IO.File.ReadAllLines(filePath);
            if (lineNumber < 1 || lineNumber > allLines.Length) return null;

            int from = Math.Max(1, lineNumber - SnippetContextLines);
            int to = Math.Min(allLines.Length, lineNumber + SnippetContextLines);

            var result = new List<CodeSnippetLineDto>();
            for (int n = from; n <= to; n++)
                result.Add(new CodeSnippetLineDto(n, allLines[n - 1], n == lineNumber));
            return result;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
