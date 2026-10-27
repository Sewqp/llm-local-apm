using CollectionServer.Data;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Services;

public sealed class TelemetryRetentionService(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<TelemetryRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Disabled by default. Historical metrics and analyses are never removed by this worker.
        var days = config.GetValue<int>("Telemetry:RetentionDays");
        if (days is < 1 or > 365) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApmDbContext>();
                var cutoff = DateTime.UtcNow.AddDays(-days);
                foreach (var (table, column) in new[] { ("Spans", "StartTime"), ("TelemetryLogs", "Timestamp"),
                    ("RuntimeSamples", "Timestamp"), ("TelemetryBatches", "ReceivedAt"), ("TraceInvestigations", "CreatedAt") })
#pragma warning disable EF1002 // Table/column names are fixed literals; the cutoff is parameterized.
                    await db.Database.ExecuteSqlRawAsync(
                        $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {column} < {{0}} LIMIT 5000)",
                        [cutoff], stoppingToken);
#pragma warning restore EF1002
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Telemetry retention failed; the next scheduled pass will retry.");
            }
        }
    }
}
