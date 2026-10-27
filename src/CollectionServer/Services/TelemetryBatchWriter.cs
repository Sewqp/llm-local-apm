using Apm.Agent;
using CollectionServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace CollectionServer.Services;

public static class TelemetryBatchWriter
{
    private static readonly SemaphoreSlim Writer = new(1);

    public static async Task<bool> WriteAsync(ApmDbContext db, TelemetryBatch batch, CancellationToken ct)
    {
        await Writer.WaitAsync(ct);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            using (var configuration = connection.CreateCommand())
            {
                // Keep durable acknowledgements. Coalesce WAL checkpoints instead of weakening fsync.
                configuration.CommandText = "PRAGMA synchronous=FULL; PRAGMA cache_size=-32768; " +
                    "PRAGMA wal_autocheckpoint=16384; PRAGMA journal_size_limit=67108864;";
                configuration.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction();
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT OR IGNORE INTO TelemetryBatches(Id,ReceivedAt) VALUES ($id,$at)";
            receipt.Parameters.AddWithValue("$id", batch.BatchId);
            receipt.Parameters.AddWithValue("$at", DateTime.UtcNow);
            if (receipt.ExecuteNonQuery() == 0) return false;
            Insert(connection, transaction, "Spans", batch.Spans, ct);
            Insert(connection, transaction, "Metrics", batch.Metrics, ct);
            Insert(connection, transaction, "TelemetryLogs", batch.Logs, ct);
            Insert(connection, transaction, "RuntimeSamples", batch.Runtime, ct);
            transaction.Commit();
            return true;
        }
        finally { Writer.Release(); }
    }

    private static class Columns<T>
    {
        public static readonly PropertyInfo[] Properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        public static readonly string Names = string.Join(',', Properties.Select(p => '"' + p.Name + '"'));
        public static readonly string Values = string.Join(',', Properties.Select((_, i) => "$p" + i));
    }

    private static void Insert<T>(SqliteConnection connection, SqliteTransaction transaction,
        string table, List<T> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        // Identifiers come only from the four internal DTO types and fixed table names above.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT OR IGNORE INTO {table} ({Columns<T>.Names}) VALUES ({Columns<T>.Values})";
        if (typeof(T) == typeof(SpanData))
            command.CommandText += """
                 ON CONFLICT(TraceId,SpanId) DO UPDATE SET
                    DetailStatus=excluded.DetailStatus, SamplingReason=excluded.SamplingReason,
                    ExpectedDetailSpans=excluded.ExpectedDetailSpans, LimitedDetailSpans=excluded.LimitedDetailSpans
                 WHERE excluded.IsEntry=1 AND excluded.DetailStatus IS NOT NULL AND excluded.DetailStatus<>'pending'
                """;
        foreach (var (property, index) in Columns<T>.Properties.Select((p, i) => (p, i)))
            command.Parameters.AddWithValue("$p" + index, property.GetValue(rows[0]) ?? DBNull.Value);
        command.Prepare();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            for (var index = 0; index < Columns<T>.Properties.Length; index++)
                command.Parameters[index].Value = Columns<T>.Properties[index].GetValue(row) ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }
}
