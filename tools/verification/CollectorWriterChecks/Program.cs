using Apm.Agent;
using CollectionServer.Data;
using CollectionServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

var path = Path.Combine(Path.GetTempPath(), "apm-writer-" + Guid.NewGuid().ToString("N")) + ".db";
var services = new ServiceCollection();
services.AddDbContext<ApmDbContext>(o => o.UseSqlite("Data Source=" + path));
services.AddSingleton<TelemetryBatchWriter>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<ApmDbContext>();
db.Database.EnsureCreated(); TelemetrySchema.Ensure(db);
db.Database.ExecuteSqlRaw("CREATE TRIGGER reject_test_metric BEFORE INSERT ON Metrics WHEN NEW.Path='reject' BEGIN SELECT RAISE(ABORT,'injected metric failure'); END");
var writer = provider.GetRequiredService<TelemetryBatchWriter>();
var tests = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); tests.Add(name); }
TelemetryBatch Batch() => new(Guid.NewGuid().ToString("N"), [new SpanData
{
    TraceId = Guid.NewGuid().ToString("N"), SpanId = Guid.NewGuid().ToString("N")[..16],
    ServiceName = "WriterChecks", InstanceId = "test", RunId = "test", Name = "Test", Category = "packet",
    StartTime = DateTime.UtcNow, IsEntry = true, Outcome = "success", CodeVersion = "test"
}], [new MetricData { Timestamp = DateTime.UtcNow, ServerName = "WriterChecks", Method = "PACKET", Path = "reject" }], [], []);
var failed = Batch();
try { await writer.WriteAsync(failed, default); Check(false, "injected transaction failure"); }
catch (SqliteException) { Check(true, "injected transaction failure"); }
Check(!db.Spans.Any() && !db.Metrics.Any() && !db.TelemetryBatches.Any(), "receipt and all rows roll back together");
failed.Metrics[0].Path = "recovered";
Check(await writer.WriteAsync(failed, default), "cached statements recover after rollback with same batch ID");
Check(!await writer.WriteAsync(failed, default), "duplicate durable batch is acknowledged without reinsertion");
var concurrent = Enumerable.Range(0, 64).Select(_ => { var b = Batch(); b.Metrics[0].Path = "parallel"; return b; }).ToList();
await Task.WhenAll(concurrent.Select(b => Task.Run(() => writer.WriteAsync(b, default))));
Check(db.Spans.Count() == 65 && db.Metrics.Count() == 65 && db.TelemetryBatches.Count() == 65, "concurrent HTTP callers share one atomic writer");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await writer.WriteAsync(Batch(), cancelled.Token); Check(false, "cancel before writer lease"); }
catch (OperationCanceledException) { Check(true, "cancel before writer lease"); }
Check(db.Metrics.Count() == 65, "cancelled request leaves no partial row");
Check(db.Database.SqlQueryRaw<string>("PRAGMA integrity_check").AsEnumerable().Single() == "ok", "SQLite integrity preserved");
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, evidence = path, tests }, new JsonSerializerOptions { WriteIndented = true }));
