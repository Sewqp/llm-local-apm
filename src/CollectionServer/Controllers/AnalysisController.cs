using CollectionServer.Data;
using CollectionServer.Dtos;
using CollectionServer.Models;
using CollectionServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Controllers;

[ApiController]
[Route("api/")]
public class AnalysisController(
    ApmDbContext db,
    IAnomalyDetectionService anomalyDetection,
    ILmStudioClient lmStudio) : ControllerBase
{
    private const int MaxLlmCallsPerRun = 5;

    // 이상탐지 결과만 빠르게 조회 (LLM 호출 없음)
    [HttpGet("anomalies")]
    public async Task<IActionResult> GetAnomalies(
        [FromQuery] int minutes = 30, [FromQuery] string? server = null, CancellationToken ct = default)
    {
        var anomalies = await anomalyDetection.DetectAsync(TimeSpan.FromMinutes(minutes), server, ct);
        return Ok(anomalies);
    }

    // 이상탐지 + LM Studio 원인분석/추천 생성 (최근 이상탐지 결과 중 경로별 최신 1건씩, 최대 MaxLlmCallsPerRun건)
    [HttpPost("analysis/run")]
    public async Task<IActionResult> RunAnalysis(
        [FromQuery] int minutes = 30, [FromQuery] string? server = null, CancellationToken ct = default)
    {
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

                 위 지표를 바탕으로 가능한 원인과 개선 방향을 설명해줘.
                 """;

            var analysisText = await lmStudio.AnalyzeAsync(prompt, ct);

            var result = new AnalysisResult
            {
                CreatedAt = DateTime.UtcNow,
                ServerName = a.ServerName,
                Path = a.Path,
                AnomalyReason = a.Reason,
                ObservedDurationMs = a.ObservedDurationMs,
                BaselineDurationMs = a.BaselineDurationMs,
                LlmAnalysis = analysisText
            };
            db.Analyses.Add(result);

            responses.Add(new AnalysisResponseDto(
                a.ServerName, a.Path, a.Timestamp, a.ObservedDurationMs, a.BaselineDurationMs,
                a.Reason, analysisText));
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

        var items = await scoped
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .Select(a => new AnalysisResponseDto(
                a.ServerName, a.Path, a.CreatedAt, a.ObservedDurationMs, a.BaselineDurationMs,
                a.AnomalyReason, a.LlmAnalysis))
            .ToListAsync(ct);

        return Ok(items);
    }
}
