using CollectionServer.Data;
using CollectionServer.Dtos;
using CollectionServer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionServer.Services;

public interface IAnomalyDetectionService
{
    Task<List<AnomalyDto>> DetectAsync(TimeSpan window, string? server = null, CancellationToken ct = default);
}

/// <summary>
/// 임계값 기반 이상탐지 룰:
///  1) HTTP 5xx 응답은 항상 이상으로 표기
///  2) 응답시간이 해당 경로(Path)의 기준선 평균 대비 N배(ThresholdMultiplier)를 초과 *하고*,
///     절대 지연 증가량도 MinAbsoluteDeltaMs 이상이면 이상으로 표기
///     - 기준선은 조회 구간(window) 이전의 과거 데이터 평균을 우선 사용
///     - 과거 데이터가 부족하면 조회 구간 내 평균으로 대체
///     - 절대값 조건이 없으면 하트비트처럼 원래 1ms 미만인 작업은 미세한 지터만으로 배수 조건을
///       쉽게 넘어 노이즈가 발생함
/// </summary>
public class AnomalyDetectionService(ApmDbContext db, IOptions<AnomalyDetectionOptions> options)
    : IAnomalyDetectionService
{
    private readonly AnomalyDetectionOptions _options = options.Value;

    public async Task<List<AnomalyDto>> DetectAsync(TimeSpan window, string? server = null, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - window;

        var historicalQuery = db.Metrics.Where(m => m.Timestamp < since);
        var recentQuery = db.Metrics.Where(m => m.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(server))
        {
            historicalQuery = historicalQuery.Where(m => m.ServerName == server);
            recentQuery = recentQuery.Where(m => m.ServerName == server);
        }

        // Preserve the baseline denominator when normal Heartbeats have no individual rows.
        var historicalBaselines = (await Baselines(since, server, true, ct))
            .Where(b => b.Count >= _options.MinBaselineSamples).ToDictionary(b => (b.ServerName, b.Path), b => b.Avg);
        var recent = await recentQuery.OrderByDescending(m => m.Timestamp).ToListAsync(ct);
        var windowFallbackBaselines = (await Baselines(since, server, false, ct))
            .ToDictionary(b => (b.ServerName, b.Path), b => b.Avg);

        var results = new List<AnomalyDto>();

        foreach (var m in recent)
        {
            var key = (m.ServerName, m.Path);

            if (m.StatusCode >= 500)
            {
                var baselineForError = historicalBaselines.GetValueOrDefault(key,
                    windowFallbackBaselines.GetValueOrDefault(key, 0));
                results.Add(new AnomalyDto(m.ServerName, m.Path, m.Timestamp, m.DurationMs,
                    baselineForError, m.StatusCode, $"서버 오류 응답 발생 (HTTP {m.StatusCode})"));
                continue;
            }

            var baseline = historicalBaselines.TryGetValue(key, out var histAvg)
                ? histAvg
                : windowFallbackBaselines.GetValueOrDefault(key, 0);

            if (baseline > 0 && m.DurationMs > baseline * _options.ThresholdMultiplier
                && m.DurationMs - baseline >= _options.MinAbsoluteDeltaMs)
            {
                var ratio = m.DurationMs / baseline;
                var delta = m.DurationMs - baseline;
                var baselineText = baseline < 1 ? $"{baseline:F2}ms" : $"{baseline:F0}ms";
                results.Add(new AnomalyDto(m.ServerName, m.Path, m.Timestamp, m.DurationMs, baseline,
                    m.StatusCode,
                    $"응답시간이 기준 평균({baselineText}) 대비 {ratio:F1}배, 절대값 기준 {delta:F1}ms 증가"));
            }
        }

        return results;
    }
    private async Task<List<BaselineRow>> Baselines(DateTime since, string? server, bool historical, CancellationToken ct)
    {
        var selected = server ?? "";
        return await db.Database.SqlQuery<BaselineRow>($"""
            WITH weighted AS (
                SELECT ServerName, Path, 1 AS N, DurationMs AS Total FROM Metrics
                WHERE ({selected}='' OR ServerName={selected}) AND
                    (({historical}=1 AND Timestamp < {since}) OR ({historical}=0 AND Timestamp >= {since}))
                UNION ALL SELECT ServiceName, '/packet/Heartbeat', Count, DurationSumMs FROM HeartbeatAggregates
                WHERE ({selected}='' OR ServiceName={selected}) AND
                    (({historical}=1 AND Timestamp < {since}) OR ({historical}=0 AND Timestamp >= {since}))
            ) SELECT ServerName, Path, SUM(N) AS Count, SUM(Total)/SUM(N) AS Avg
            FROM weighted GROUP BY ServerName, Path
            """).ToListAsync(ct);
    }
    public sealed class BaselineRow
    {
        public string ServerName { get; set; } = "";
        public string Path { get; set; } = "";
        public long Count { get; set; }
        public double Avg { get; set; }
    }

}
