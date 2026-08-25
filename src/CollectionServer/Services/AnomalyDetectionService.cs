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

        // 서버가 여러 대 섞여 있어도 기준선이 서로 오염되지 않도록 (서버, 경로) 조합으로 그룹핑
        var historicalBaselines = (await historicalQuery
            .GroupBy(m => new { m.ServerName, m.Path })
            .Select(g => new { g.Key.ServerName, g.Key.Path, Avg = g.Average(m => m.DurationMs), Count = g.Count() })
            .Where(g => g.Count >= _options.MinBaselineSamples)
            .ToListAsync(ct))
            .ToDictionary(g => (g.ServerName, g.Path), g => g.Avg);

        var recent = await recentQuery
            .OrderByDescending(m => m.Timestamp)
            .ToListAsync(ct);

        var windowFallbackBaselines = recent
            .GroupBy(m => (m.ServerName, m.Path))
            .ToDictionary(g => g.Key, g => g.Average(m => m.DurationMs));

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
}
