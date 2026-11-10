using Apm.Agent;
using CollectionServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics;

namespace CollectionServer.Services;

public sealed class TelemetryBatchWriter(IServiceScopeFactory scopes) : IDisposable
{
    private readonly SemaphoreSlim _writer = new(1);
    private SqliteConnection? _connection;
    private SqliteCommand? _receipt;
    private PreparedInsert<SpanData>? _spans;
    private PreparedInsert<MetricData>? _metrics;
    private PreparedInsert<LogData>? _logs;
    private PreparedInsert<RuntimeData>? _runtime;

    public async Task<bool> WriteAsync(TelemetryBatch batch, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await _writer.WaitAsync(ct);
        var acquired = Stopwatch.GetTimestamp();
        try
        {
            EnsureConnection();
            using var transaction = _connection!.BeginTransaction();
            _receipt!.Transaction = transaction;
            _receipt.Parameters[0].Value = batch.BatchId;
            _receipt.Parameters[1].Value = DateTime.UtcNow;
            if (_receipt.ExecuteNonQuery() == 0) return false;
            _spans!.Write(transaction, batch.Spans, ct);
            _metrics!.Write(transaction, batch.Metrics, ct);
            _logs!.Write(transaction, batch.Logs, ct);
            _runtime!.Write(transaction, batch.Runtime, ct);
            var inserted = Stopwatch.GetTimestamp();
            transaction.Commit();
            if (Environment.GetEnvironmentVariable("APM_INGEST_TIMINGS") == "1")
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ingest = true, spans = batch.Spans.Count,
                    waitMs = Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds,
                    insertMs = Stopwatch.GetElapsedTime(acquired, inserted).TotalMilliseconds,
                    commitMs = Stopwatch.GetElapsedTime(inserted).TotalMilliseconds }));
            return true;
        }
        finally { _writer.Release(); }
    }

    private void EnsureConnection()
    {
        if (_connection is not null) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApmDbContext>();
        var settings = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
        var connection = new SqliteConnection(settings.ToString());
        try
        {
            connection.Open();
            using var configuration = connection.CreateCommand();
            // One writer keeps the hot index pages and prepared statements between HTTP batches.
            // FULL still acknowledges only durable commits; this is a cache budget, not a queue expansion.
            configuration.CommandText = "PRAGMA synchronous=FULL; PRAGMA cache_size=-524288; " +
                "PRAGMA wal_autocheckpoint=0; PRAGMA journal_size_limit=67108864;";
            configuration.ExecuteNonQuery();
            _receipt = connection.CreateCommand();
            _receipt.CommandText = "INSERT OR IGNORE INTO TelemetryBatches(Id,ReceivedAt) VALUES ($id,$at)";
            _receipt.Parameters.AddWithValue("$id", "");
            _receipt.Parameters.AddWithValue("$at", DateTime.UtcNow);
            _receipt.Prepare();
            _spans = new(connection, "Spans");
            _metrics = new(connection, "Metrics");
            _logs = new(connection, "TelemetryLogs");
            _runtime = new(connection, "RuntimeSamples");
            _connection = connection;
        }
        catch
        {
            DisposeCommands();
            connection.Dispose();
            throw;
        }
    }

    private sealed class PreparedInsert<T> : IDisposable
    {
        private static readonly PropertyInfo[] Properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        private static readonly Func<T, object?>[] Read = Properties.Select(property =>
        {
            var row = Expression.Parameter(typeof(T));
            return Expression.Lambda<Func<T, object?>>(Expression.Convert(Expression.Property(row, property), typeof(object)), row).Compile();
        }).ToArray();
        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _parameters;

        public PreparedInsert(SqliteConnection connection, string table)
        {
            _command = connection.CreateCommand();
            var names = string.Join(',', Properties.Select(p => '"' + p.Name + '"'));
            var values = string.Join(',', Properties.Select((_, i) => "$p" + i));
            _command.CommandText = $"INSERT OR IGNORE INTO {table} ({names}) VALUES ({values})";
            if (typeof(T) == typeof(SpanData))
                _command.CommandText += """
                     ON CONFLICT(TraceId,SpanId) DO UPDATE SET
                        DetailStatus=excluded.DetailStatus, SamplingReason=excluded.SamplingReason,
                        ExpectedDetailSpans=excluded.ExpectedDetailSpans, LimitedDetailSpans=excluded.LimitedDetailSpans
                     WHERE excluded.IsEntry=1 AND excluded.DetailStatus IS NOT NULL AND excluded.DetailStatus<>'pending'
                    """;
            _parameters = Properties.Select((_, i) => _command.Parameters.AddWithValue("$p" + i, DBNull.Value)).ToArray();
            _command.Prepare();
        }

        public void Write(SqliteTransaction transaction, List<T> rows, CancellationToken ct)
        {
            _command.Transaction = transaction;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                for (var index = 0; index < Read.Length; index++)
                    _parameters[index].Value = Read[index](row) ?? DBNull.Value;
                _command.ExecuteNonQuery();
            }
        }

        public void Dispose() => _command.Dispose();
    }

    private void DisposeCommands()
    {
        _receipt?.Dispose(); _spans?.Dispose(); _metrics?.Dispose(); _logs?.Dispose(); _runtime?.Dispose();
    }

    public void Dispose()
    {
        _writer.Wait();
        try { DisposeCommands(); _connection?.Dispose(); }
        finally { _writer.Release(); }
    }
}
