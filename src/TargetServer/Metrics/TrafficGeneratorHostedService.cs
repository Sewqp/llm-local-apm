using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace TargetServer.Metrics;

/// <summary>
/// 데모/시연 편의를 위해 자체 트래픽을 생성하는 백그라운드 워커.
/// 실제 "기존 보유 C# 서버"에는 필요 없으며, 데모 대상 서버 전용.
/// </summary>
public class TrafficGeneratorHostedService(IHttpClientFactory httpClientFactory, IServer server, ILogger<TrafficGeneratorHostedService> logger)
    : BackgroundService
{
    private static readonly Random Rng = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var baseUrl = addresses?.FirstOrDefault(a => a.StartsWith("http://")) ?? "http://localhost:5082";

        var http = httpClientFactory.CreateClient(nameof(TrafficGeneratorHostedService));
        http.BaseAddress = new Uri(baseUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var roll = Rng.NextDouble();
                if (roll < 0.4)
                    await http.GetAsync("/api/products", stoppingToken);
                else if (roll < 0.75)
                    await http.GetAsync($"/api/products/{Rng.Next(1, 20)}", stoppingToken);
                else if (roll < 0.9)
                    await http.GetAsync("/api/orders", stoppingToken);
                else
                    await http.PostAsync("/api/orders", null, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "자체 트래픽 생성 요청 실패");
            }

            await Task.Delay(Rng.Next(150, 400), stoppingToken);
        }
    }
}
