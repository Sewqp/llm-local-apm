using Microsoft.EntityFrameworkCore;
using CollectionServer.Data;

namespace CollectionServer.Services;

public static class TelemetrySchema
{
    public static void Ensure(ApmDbContext db)
    {
        foreach (var column in new[] { "TraceId", "RunId", "Outcome", "UserKey", "SessionId" })
        {
            var present = db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM pragma_table_info('Metrics') WHERE name = {0}", column).Single();
            // Identifiers come only from the fixed list above; values cannot parameterize identifiers.
#pragma warning disable EF1002
            if (present == 0) db.Database.ExecuteSqlRaw($"ALTER TABLE Metrics ADD COLUMN {column} TEXT NULL");
#pragma warning restore EF1002
        }
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Spans (
                TraceId TEXT NOT NULL, SpanId TEXT NOT NULL, ParentSpanId TEXT NULL,
                RunId TEXT NOT NULL, ServiceName TEXT NOT NULL, InstanceId TEXT NOT NULL,
                Name TEXT NOT NULL, Category TEXT NOT NULL, StartTime TEXT NOT NULL,
                DurationMs REAL NOT NULL, Outcome TEXT NOT NULL, ResultCode TEXT NULL,
                UserKey TEXT NULL, SessionId TEXT NULL, IsEntry INTEGER NOT NULL,
                ErrorType TEXT NULL, CodeVersion TEXT NOT NULL, PRIMARY KEY(TraceId, SpanId));
            CREATE INDEX IF NOT EXISTS IX_Spans_ServiceName_StartTime ON Spans(ServiceName, StartTime);
            CREATE INDEX IF NOT EXISTS IX_Spans_IsEntry_StartTime ON Spans(IsEntry, StartTime);
            CREATE INDEX IF NOT EXISTS IX_Spans_User_Start ON Spans(UserKey, StartTime);
            CREATE INDEX IF NOT EXISTS IX_Spans_Session_Start ON Spans(SessionId, StartTime);
            CREATE TABLE IF NOT EXISTS TelemetryLogs (
                Id TEXT PRIMARY KEY, Timestamp TEXT NOT NULL, TraceId TEXT NOT NULL,
                SpanId TEXT NOT NULL, ServiceName TEXT NOT NULL, Level TEXT NOT NULL,
                Message TEXT NOT NULL, ErrorType TEXT NULL, StackTrace TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_Logs_Trace ON TelemetryLogs(TraceId, Timestamp);
            CREATE TABLE IF NOT EXISTS RuntimeSamples (
                Id TEXT PRIMARY KEY, Timestamp TEXT NOT NULL, ServiceName TEXT NOT NULL,
                InstanceId TEXT NOT NULL, RunId TEXT NOT NULL, CpuPercent REAL NOT NULL,
                WorkingSetMb REAL NOT NULL, GcHeapMb REAL NOT NULL,
                ThreadPoolQueue INTEGER NOT NULL, ThreadPoolThreads INTEGER NOT NULL,
                ActiveSessions INTEGER NOT NULL, QueueDepth INTEGER NOT NULL,
                Dropped INTEGER NOT NULL, FailedBatches INTEGER NOT NULL, Sent INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Runtime_Service_Time ON RuntimeSamples(ServiceName, Timestamp);
            CREATE TABLE IF NOT EXISTS TelemetryBatches (Id TEXT PRIMARY KEY, ReceivedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Metrics_Server_Time ON Metrics(ServerName, Timestamp);
            CREATE TABLE IF NOT EXISTS TraceInvestigations (
                Id TEXT PRIMARY KEY, TraceId TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                Model TEXT NOT NULL, PromptVersion TEXT NOT NULL, EvidenceJson TEXT NOT NULL,
                Answer TEXT NOT NULL, Status TEXT NOT NULL, DurationMs REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Investigations_Trace ON TraceInvestigations(TraceId, CreatedAt);
            """);
        AddColumn(db, "Spans", "DetailStatus", "TEXT NULL");
        AddColumn(db, "Spans", "SamplingReason", "TEXT NULL");
        foreach (var column in new[] { "RequestMethod", "Route", "SourceFile", "LinkedTraceId", "LinkedSpanId" })
            AddColumn(db, "Spans", column, "TEXT NULL");
        AddColumn(db, "Spans", "HttpStatusCode", "INTEGER NULL");
        AddColumn(db, "Spans", "ExpectedDetailSpans", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Spans", "LimitedDetailSpans", "INTEGER NOT NULL DEFAULT 0");
        foreach (var column in new[] { "RequestQueueDepth", "RequestsObserved", "RequestsSent", "RequestsDropped",
            "DetailsDropped", "SampledOutTraces", "LimitedTraces", "UncorrelatedSpansSkipped" })
            AddColumn(db, "RuntimeSamples", column, "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "RuntimeSamples", "DetailSampleRate", "REAL NOT NULL DEFAULT 0");
        RemoveDuplicateIndex(db, "IX_Spans_Service_Start", "ServiceName", "StartTime");
        RemoveDuplicateIndex(db, "IX_Spans_Entry_Start", "IsEntry", "StartTime");
    }

    private static void RemoveDuplicateIndex(ApmDbContext db, string index, string first, string second)
    {
        // Only remove our old, non-unique, non-partial indexes with these exact columns.
        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_index_list('Spans') WHERE name=$name AND \"unique\"=0 AND partial=0";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = index;
            command.Parameters.Add(parameter);
            if (command.ExecuteScalar() is null) return;
            command.CommandText = "SELECT name FROM pragma_index_info($name) ORDER BY seqno";
            var columns = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
            if (!columns.SequenceEqual(new[] { first, second })) return;
            command.Parameters.Clear();
            command.CommandText = "DROP INDEX \"" + index + "\"";
            command.ExecuteNonQuery();
        }
        finally { db.Database.CloseConnection(); }
    }

    private static void AddColumn(ApmDbContext db, string table, string column, string definition)
    {
        // Every identifier and definition comes from a fixed literal in Ensure().
#pragma warning disable EF1002
        var exists = db.Database.SqlQueryRaw<int>(
            $"SELECT COUNT(*) AS Value FROM pragma_table_info('{table}') WHERE name = {{0}}", column).Single();
        if (exists == 0) db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
#pragma warning restore EF1002
    }
}
