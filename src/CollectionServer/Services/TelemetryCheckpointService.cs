using CollectionServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Services;

public sealed class TelemetryCheckpointService(IServiceScopeFactory scopes,
    ILogger<TelemetryCheckpointService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApmDbContext>();
        var settings = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false, DefaultTimeout = 1 };
        using var connection = new SqliteConnection(settings.ToString());
        await connection.OpenAsync(stoppingToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        if (version < new Version(3, 51, 3))
            throw new InvalidOperationException("Concurrent WAL checkpointing requires SQLite 3.51.3 or newer (WAL-reset fix).");
        // WAL commits stay FULL/durable. Moving old pages into the main DB is independent
        // of acknowledgement, and PASSIVE never waits for a reader to finish its snapshot.
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { command.ExecuteNonQuery(); }
                catch (SqliteException ex) { logger.LogWarning(ex, "Telemetry WAL checkpoint will retry."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
