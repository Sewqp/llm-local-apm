using CollectionServer.Data;
using CollectionServer.Dtos;
using CollectionServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Controllers;

[ApiController]
[Route("api/metrics")]
public class MetricsController(ApmDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Ingest([FromBody] MetricIngestDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.ServerName) || dto.ServerName.Length > 100 ||
            string.IsNullOrWhiteSpace(dto.Path) || dto.Path.Length > 250 ||
            string.IsNullOrWhiteSpace(dto.Method) || dto.Method.Length > 20 ||
            !double.IsFinite(dto.DurationMs) || dto.DurationMs is < 0 or > 86400000 ||
            dto.StatusCode is < 100 or > 599 || dto.Timestamp > DateTime.UtcNow.AddMinutes(5)) return BadRequest();
        db.Metrics.Add(new MetricRecord
        {
            Timestamp = dto.Timestamp, ServerName = dto.ServerName, Method = dto.Method,
            Path = dto.Path, StatusCode = dto.StatusCode, DurationMs = dto.DurationMs
        });
        await db.SaveChangesAsync(ct);
        return Accepted();
    }

    [HttpGet("server-names")]
    public async Task<IActionResult> ServerNames(int minutes = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 1440));
        return Ok(await db.Metrics.Where(m => m.Timestamp >= since).Select(m => m.ServerName)
            .Distinct().OrderBy(n => n).Take(100).ToListAsync(ct));
    }

    [HttpGet]
    public async Task<IActionResult> Query(int minutes = 30, string? path = null, string? server = null,
        int limit = 2000, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440 || limit is < 1 or > 10000) return BadRequest();
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var query = db.Metrics.AsNoTracking().Where(m => m.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(path)) query = query.Where(m => m.Path == path);
        if (!string.IsNullOrWhiteSpace(server)) query = query.Where(m => m.ServerName == server);
        var items = await query.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id).Take(limit + 1).ToListAsync(ct);
        Response.Headers["X-APM-Truncated"] = items.Count > limit ? "true" : "false";
        return Ok(items.Take(limit).OrderBy(m => m.Timestamp));
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Telemetry:AllowDestructiveReset"))
            return StatusCode(403, new { error = "원본 보호를 위해 초기화가 비활성화되어 있습니다. 별도 테스트 DB를 사용하세요." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Metrics.ExecuteDeleteAsync(ct);
        await db.Analyses.ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(int minutes = 30, string? server = null, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440) return BadRequest();
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var selected = server ?? "";
        // SQLite computes exact nearest-rank percentiles; no full duration list is materialized in the API process.
        var rows = await db.Database.SqlQuery<SummaryRow>($"""
            WITH ranked AS (
                SELECT Path, DurationMs, StatusCode, Outcome,
                    ROW_NUMBER() OVER(PARTITION BY Path ORDER BY DurationMs) AS rn,
                    COUNT(*) OVER(PARTITION BY Path) AS n
                FROM Metrics WHERE Method <> 'BACKGROUND' AND Timestamp >= {since} AND ({selected} = '' OR ServerName = {selected})
            )
            SELECT Path, COUNT(*) AS Count, AVG(DurationMs) AS AvgDurationMs,
                MAX(CASE WHEN rn = (n * 50 + 99) / 100 THEN DurationMs END) AS P50DurationMs,
                MAX(CASE WHEN rn = (n * 95 + 99) / 100 THEN DurationMs END) AS P95DurationMs,
                MAX(CASE WHEN rn = (n * 99 + 99) / 100 THEN DurationMs END) AS P99DurationMs,
                1.0 * SUM(CASE WHEN StatusCode >= 500 THEN 1 ELSE 0 END) / COUNT(*) AS ErrorRate,
                1.0 * SUM(CASE WHEN Outcome = 'rejected' THEN 1 ELSE 0 END) / COUNT(*) AS RejectedRate,
                1.0 * COUNT(*) / ({minutes} * 60) AS RequestsPerSecond
            FROM ranked GROUP BY Path ORDER BY AvgDurationMs DESC
            """).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("timeseries")]
    public async Task<IActionResult> Timeseries(int minutes = 30, string? server = null, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440) return BadRequest();
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var selected = server ?? "";
        return Ok(await db.Database.SqlQuery<TimeseriesRow>($"""
            SELECT (CAST(strftime('%s', Timestamp) AS INTEGER) / 60) * 60000 AS Time,
                AVG(DurationMs) AS AvgDurationMs, COUNT(*) AS RequestCount,
                1.0 * SUM(CASE WHEN StatusCode >= 500 THEN 1 ELSE 0 END) / COUNT(*) AS ErrorRate
            FROM Metrics WHERE Method <> 'BACKGROUND' AND Timestamp >= {since} AND ({selected} = '' OR ServerName = {selected})
            GROUP BY Time ORDER BY Time
            """).ToListAsync(ct));
    }

    public sealed class SummaryRow
    {
        public string Path { get; set; } = "";
        public long Count { get; set; }
        public double AvgDurationMs { get; set; }
        public double P50DurationMs { get; set; }
        public double P95DurationMs { get; set; }
        public double P99DurationMs { get; set; }
        public double ErrorRate { get; set; }
        public double RejectedRate { get; set; }
        public double RequestsPerSecond { get; set; }
    }

    public sealed class TimeseriesRow
    {
        public long Time { get; set; }
        public double AvgDurationMs { get; set; }
        public long RequestCount { get; set; }
        public double ErrorRate { get; set; }
    }
}
