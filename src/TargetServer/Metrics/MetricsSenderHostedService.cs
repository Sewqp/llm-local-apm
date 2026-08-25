using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TargetServer.Options;

namespace TargetServer.Metrics;

/// <summary>
/// 큐에 쌓인 계측 이벤트를 수집 서버(POST /api/metrics)로 전송하는 백그라운드 워커.
/// 수집 서버가 잠시 꺼져 있어도 대상 서버 자체 응답에는 영향을 주지 않는다.
/// </summary>
public class MetricsSenderHostedService(
    MetricsQueue queue,
    IHttpClientFactory httpClientFactory,
    IOptions<MetricsOptions> options,
    ILogger<MetricsSenderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpClientFactory.CreateClient(nameof(MetricsSenderHostedService));
        http.BaseAddress = new Uri(options.Value.CollectionServerBaseUrl);

        await foreach (var metricEvent in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var response = await http.PostAsJsonAsync("/api/metrics", metricEvent, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("수집 서버 전송 실패: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "수집 서버 전송 중 예외 발생 (수집 서버가 실행 중인지 확인하세요)");
            }
        }
    }
}
