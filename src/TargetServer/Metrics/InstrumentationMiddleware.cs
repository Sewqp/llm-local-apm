using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TargetServer.Options;

namespace TargetServer.Metrics;

/// <summary>
/// 모든 요청의 시작/종료 시각, 상태코드, 경로를 계측하여 큐에 적재한다.
/// 기존 서버에 한 줄(app.UseMiddleware&lt;InstrumentationMiddleware&gt;())만 추가하면 적용 가능하도록 설계.
/// </summary>
public partial class InstrumentationMiddleware(RequestDelegate next, MetricsQueue queue, IOptions<MetricsOptions> options)
{
    // /api/products/9, /api/products/16 처럼 ID가 섞인 경로를 /api/products/{id} 로 묶어
    // 이상탐지 기준선(baseline) 표본이 경로별로 충분히 쌓이도록 정규화한다.
    [GeneratedRegex(@"/\d+(?=/|$)")]
    private static partial Regex NumericSegmentRegex();

    private static string NormalizePath(string rawPath) => NumericSegmentRegex().Replace(rawPath, "/{id}");

    public async Task InvokeAsync(HttpContext context)
    {
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();

            queue.Enqueue(new MetricEvent(
                Timestamp: startTime,
                ServerName: options.Value.ServerName,
                Method: context.Request.Method,
                Path: NormalizePath(context.Request.Path.Value ?? "/"),
                StatusCode: context.Response.StatusCode,
                DurationMs: stopwatch.Elapsed.TotalMilliseconds));
        }
    }
}
