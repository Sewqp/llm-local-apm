using CollectionServer.Data;
using CollectionServer.Dtos;
using CollectionServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Controllers;

[ApiController]
[Route("api/metrics")]
public class MetricsController(ApmDbContext db) : ControllerBase
{
    // 대상 서버의 계측 미들웨어가 요청 1건마다 호출
    [HttpPost]
    public async Task<IActionResult> Ingest([FromBody] MetricIngestDto dto, CancellationToken ct)
    {
        var record = new MetricRecord
        {
            Timestamp = dto.Timestamp,
            ServerName = dto.ServerName,
            Method = dto.Method,
            Path = dto.Path,
            StatusCode = dto.StatusCode,
            DurationMs = dto.DurationMs
        };

        db.Metrics.Add(record);
        await db.SaveChangesAsync(ct);

        return Accepted();
    }

    // 대시보드 서버 선택 드롭다운용 — 최근 구간에 데이터가 있는 서버 이름 목록
    [HttpGet("server-names")]
    public async Task<IActionResult> ServerNames([FromQuery] int minutes = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var names = await db.Metrics
            .Where(m => m.Timestamp >= since)
            .Select(m => m.ServerName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

        return Ok(names);
    }

    // 대시보드 시계열 그래프용 원본 데이터 조회
    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] int minutes = 30,
        [FromQuery] string? path = null,
        [FromQuery] string? server = null,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var query = db.Metrics.Where(m => m.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(path))
            query = query.Where(m => m.Path == path);
        if (!string.IsNullOrWhiteSpace(server))
            query = query.Where(m => m.ServerName == server);

        var items = await query
            .OrderBy(m => m.Timestamp)
            .Select(m => new
            {
                m.Id,
                m.Timestamp,
                m.ServerName,
                m.Method,
                m.Path,
                m.StatusCode,
                m.DurationMs
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    // 데모/테스트 반복용 초기화 — 메트릭·분석 이력을 전부 비움(서버 등록 목록 등 설정은 안 건드림).
    // 대상은 지금 CollectionServer가 실제로 쓰고 있는 DB 파일 하나뿐이라(dotnet run 기준
    // bin/.../apm.db), src/CollectionServer/apm.db에 쌓인 과거 실측 데이터와는 무관함.
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await db.Metrics.ExecuteDeleteAsync(ct);
        await db.Analyses.ExecuteDeleteAsync(ct);
        return NoContent();
    }

    // 대시보드 상단 요약 카드 / 경로별 통계용
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] int minutes = 30, [FromQuery] string? server = null, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddMinutes(-minutes);

        var scoped = db.Metrics.Where(m => m.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(server))
            scoped = scoped.Where(m => m.ServerName == server);

        var rows = await scoped
            .GroupBy(m => m.Path)
            .Select(g => new
            {
                Path = g.Key,
                Count = g.LongCount(),
                AvgDurationMs = g.Average(m => m.DurationMs),
                ErrorCount = g.Count(m => m.StatusCode >= 400),
                Durations = g.Select(m => m.DurationMs).ToList()
            })
            .ToListAsync(ct);

        var summaries = rows.Select(r =>
        {
            var sorted = r.Durations.OrderBy(d => d).ToList();
            var p95Index = Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1);
            var p95 = sorted.Count > 0 ? sorted[Math.Max(0, p95Index)] : 0;

            return new MetricSummaryDto(
                r.Path,
                r.Count,
                Math.Round(r.AvgDurationMs, 1),
                Math.Round(p95, 1),
                r.Count > 0 ? Math.Round((double)r.ErrorCount / r.Count, 4) : 0);
        }).OrderByDescending(s => s.AvgDurationMs);

        return Ok(summaries);
    }
}
