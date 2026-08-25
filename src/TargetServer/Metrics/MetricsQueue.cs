using System.Threading.Channels;

namespace TargetServer.Metrics;

/// <summary>
/// 계측 미들웨어가 요청 처리를 지연시키지 않도록, 수집 서버 전송은 채널을 통해 백그라운드로 위임한다.
/// </summary>
public class MetricsQueue
{
    private readonly Channel<MetricEvent> _channel =
        Channel.CreateUnbounded<MetricEvent>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(MetricEvent metricEvent) => _channel.Writer.TryWrite(metricEvent);

    public ChannelReader<MetricEvent> Reader => _channel.Reader;
}
