using Apm.Agent;
using CollectionServer.Controllers;
using CollectionServer.Data;
using CollectionServer.Services;
using CollectionServer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "heartbeat-checks-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var dbPath = Path.Combine(output, "checks.db");
if (File.Exists(dbPath)) throw new Exception("Refusing to reuse evidence DB");
var services = new ServiceCollection();
services.AddDbContext<ApmDbContext>(o=>o.UseSqlite("Data Source="+dbPath));
services.AddSingleton<TelemetryBatchWriter>();
using var provider=services.BuildServiceProvider();
using var scope=provider.CreateScope();
var db=scope.ServiceProvider.GetRequiredService<ApmDbContext>();
db.Database.EnsureCreated(); TelemetrySchema.Ensure(db);
var writer=provider.GetRequiredService<TelemetryBatchWriter>();
var ingest=new TelemetryController(writer);
var checks=new List<string>();
void Check(bool ok,string name) { if(!ok) throw new Exception("FAIL: "+name); checks.Add(name); }
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
var batches=new ConcurrentQueue<TelemetryBatch>();
var failures=0;
HttpMessageHandler Handler(bool retry=false) => new Handler(async (request,ct)=> {
    var batch=(await request.Content!.ReadFromJsonAsync<TelemetryBatch>(json,ct))!;
    batches.Enqueue(batch);
    if(batch.Aggregates.Count>0 && request.RequestUri!.AbsolutePath!="/api/telemetry/batch-with-heartbeats") throw new Exception("aggregate route requires upgraded collector");
    var response=await ingest.Ingest(batch,ct);
    if(response is BadRequestObjectResult) { File.WriteAllText(Path.Combine(output,"rejected-"+batch.BatchId+".json"),JsonSerializer.Serialize(batch)); return new(HttpStatusCode.BadRequest); }
    // The receipt committed, but the first acknowledgement is lost.
    if(retry && Interlocked.Increment(ref failures)==1) return new(HttpStatusCode.ServiceUnavailable);
    return new(HttpStatusCode.Accepted);
});
var ids=new Dictionary<string,string>();
var options=new AgentOptions { ServiceName="HeartbeatChecks", RunId="checks", CodeVersion="injected-fixtures",
    AggregateHeartbeats=true, TraceUserIds=new long[]{42}, UserHashKey=new byte[32], DetailSampleRate=0,
    SlowTraceMs=100, MaxBufferedTraces=16384, DetailFlushDelay=TimeSpan.FromMilliseconds(400), FlushInterval=TimeSpan.FromMilliseconds(5) };
AgentHealth? final=null;
await using(var agent=new LocalTelemetry(options,Handler(true))) {
    // Concurrency plus bucket rollover. No wall-clock sleeps are used to manufacture a histogram.
    Parallel.For(0,1000,_=> { using var r=agent.StartRequest("Heartbeat",Guid.NewGuid(),11);
        using var child=LocalTelemetry.StartSpan("heartbeat.child"); LocalTelemetry.SetOutcome("success"); });
    foreach(var kind in new[]{"error","child_error","late_error","slow","selected","late_identity","rejected","cancelled","unknown"}) {
        ActivityContext context;
        using(var r=agent.StartRequest("Heartbeat",Guid.NewGuid(),kind=="selected"?42:11)) {
            ids[kind]=r!.TraceId.ToHexString();context=r.Context;
            using(var child=LocalTelemetry.StartSpan("fixture."+kind)) {
                if(kind=="slow") await Task.Delay(150);
                if(kind=="child_error") child!.SetStatus(ActivityStatusCode.Error);
            }
            LocalTelemetry.SetOutcome(kind is "rejected" or "cancelled" or "unknown"?kind:"success");
            if(kind=="error") LocalTelemetry.RecordException(new InvalidOperationException("PRIVATE_PAYLOAD"));
            if(kind=="late_identity") r.SetTag("apm.user",agent.UserKey(42));
        }
        if(kind=="late_error") {
            using var source=new ActivitySource("OpenTelemetry.Instrumentation.StackExchangeRedis.Checks");
            using var late=source.StartActivity("late.error",ActivityKind.Client,context);
            late!.SetStatus(ActivityStatusCode.Error);
        }
    }
    using(agent.StartRequest("OtherPacket",Guid.NewGuid(),11)) LocalTelemetry.SetOutcome("success");
    using(agent.StartHttpRequest("GET","/Heartbeat",Guid.NewGuid(),11)) LocalTelemetry.SetOutcome("success");
    using(agent.StartRequest("SelectedOther",Guid.NewGuid(),42)) {
        using var child=LocalTelemetry.StartSpan("selected.other.child");LocalTelemetry.SetOutcome("success");
    }
    await Task.Delay(6500);
    final=agent.Sender.Health;
    File.WriteAllText(Path.Combine(output,"health-before-assert.json"),JsonSerializer.Serialize(final));
    Console.WriteLine(JsonSerializer.Serialize(final));
    Check(final.RequestsObserved==1012 && final.RequestsSent==1012,"weighted observed/sent counts include aggregates exactly once");
    Check(final.RequestsDropped==0 && final.DetailsDropped==0 && final.QueueDepth==0,"all buffered aggregates and trace queues drain without loss");
}
var aggregateCount=db.HeartbeatAggregates.Sum(a=>a.Count);
Check(aggregateCount>900,"normal concurrent Heartbeats are aggregated");
Check(db.Metrics.LongCount()+aggregateCount==1012,"aggregate plus retained metric ledger equals inputs");
Check(db.HeartbeatAggregates.All(a=>a.ServiceName=="HeartbeatChecks"),"aggregate service is isolated");
Check(db.HeartbeatAggregates.AsEnumerable().All(a=>JsonSerializer.Deserialize<long[]>(a.Histogram)!.Sum()==a.Count),"histogram count conserves requests");
Check(!JsonSerializer.Serialize(batches).Contains("PRIVATE_PAYLOAD"),"error text remains redacted");
foreach(var (kind,id) in ids) {
    var root=db.Spans.Single(s=>s.TraceId==id && s.IsEntry);
    Check(root.DetailStatus=="retained" && db.Spans.Count(s=>s.TraceId==id)>1,kind+" retains root and child detail");
    if(kind is "selected" or "late_identity") Check(root.SamplingReason=="selected_user" && root.UserKey!=null,kind+" forces detail without random sampling");
    if(kind.Contains("error")) Check(root.SamplingReason=="error",kind+" bypasses aggregation");
}
Check(db.TelemetryLogs.Count()==1,"root error log survives deferred aggregation decision");
Check(db.Spans.Any(s=>s.Name=="selected.other.child"),"selected user applies beyond Heartbeat");
Check(!batches.SelectMany(b=>b.Aggregates).Any(a=>JsonSerializer.Serialize(a).Contains("user",StringComparison.OrdinalIgnoreCase)),"aggregate wire data has no user identity");
var metrics=new MetricsController(db,new ConfigurationBuilder().Build());
var summary=(List<MetricsController.SummaryRow>)((OkObjectResult)await metrics.Summary(30,"HeartbeatChecks")).Value!;
var hb=summary.Single(s=>s.Path=="/packet/Heartbeat");
Check(hb.Count==1009 && hb.AggregatedCount==aggregateCount,"summary includes both disjoint streams without double counting");
var sum=db.HeartbeatAggregates.Sum(a=>a.DurationSumMs)+db.Metrics.Where(m=>m.Path=="/packet/Heartbeat").Sum(m=>m.DurationMs);
Check(Math.Abs(hb.AvgDurationMs-sum/1009)<1e-8,"summary preserves exact weighted mean");
Check(Math.Abs(hb.ErrorRate-1d/1009)<1e-10 && Math.Abs(hb.RejectedRate-1d/1009)<1e-10,"error and rejection denominators include normal aggregates");
Check(hb.PercentileMethod=="histogram_upper_bound" && summary.Where(s=>s.Path!="/packet/Heartbeat").All(s=>s.PercentileMethod=="exact"),"percentile approximation is explicit only for aggregated paths");
var ts=(List<MetricsController.TimeseriesRow>)((OkObjectResult)await metrics.Timeseries(30,"HeartbeatChecks")).Value!;
Check(ts.Sum(t=>t.RequestCount)==1012,"timeseries count agrees with ledger");
var anomaly=new AnomalyDetectionService(db,Options.Create(new AnomalyDetectionOptions()));
var anomalies=await anomaly.DetectAsync(TimeSpan.FromMinutes(30),"HeartbeatChecks");
Check(anomalies.Any(a=>a.ObservedDurationMs>=100),"anomaly baseline includes fast aggregate requests");
// Replay exactly the same aggregate under a new batch ID: immutable aggregate identity also deduplicates it.
var first=db.HeartbeatAggregates.AsNoTracking().First();
var duplicate=new TelemetryBatch(Guid.NewGuid().ToString("N"),[],[],[],[]){Aggregates=[first]};
await ingest.Ingest(duplicate,default);
Check(db.HeartbeatAggregates.Sum(a=>a.Count)==aggregateCount,"aggregate ID deduplicates even across new batch IDs");
var invalid=JsonSerializer.Deserialize<HeartbeatAggregate>(JsonSerializer.Serialize(first))!;
invalid.Id=Guid.NewGuid().ToString("N");invalid.Count++;
Check(await ingest.Ingest(new(Guid.NewGuid().ToString("N"),[],[],[],[]){Aggregates=[invalid]},default) is BadRequestObjectResult,"invalid histogram rejected atomically");
// Unsupported old collector rejects the aggregate-specific route before acknowledging its contents.
var lost=new LocalTelemetry(new AgentOptions { AggregateHeartbeats=true,DetailFlushDelay=TimeSpan.Zero,
    SlowTraceMs=10000,MaxAttempts=1,FlushInterval=TimeSpan.FromMilliseconds(5) },
    new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
for(int i=0;i<25;i++){using var r=lost.StartRequest("Heartbeat",Guid.NewGuid(),1);LocalTelemetry.SetOutcome("success");}
await lost.DisposeAsync();
Check(lost.Sender.Health.RequestsDropped==25 && lost.Sender.Health.DetailsDropped==0,
    "old collector route rejection reports weighted request loss, never negative detail loss");
// Force collector outage, dispose, then restart the sender against the same spool.
var spool=Path.Combine(output,"spool");
var outage=new AgentOptions {ServiceName="SpoolAggregate",RunId="outage",AggregateHeartbeats=true,
    DetailFlushDelay=TimeSpan.Zero,SlowTraceMs=10000,DetailSampleRate=0,SpoolDirectory=spool,MaxAttempts=1,FlushInterval=TimeSpan.FromMilliseconds(5)};
await using(var agent=new LocalTelemetry(outage,new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
    for(int i=0;i<100;i++){using var r=agent.StartRequest("Heartbeat",Guid.NewGuid(),1);LocalTelemetry.SetOutcome("success");}
Check(Directory.EnumerateFiles(spool,"*.json",SearchOption.AllDirectories).Any(),"aggregate batch persists on collector outage");
await using(var sender=new TelemetrySender(outage,"replacement-instance",Handler())) {
    for(int i=0;i<60 && sender.Health.SpoolPendingBatches>0;i++) await Task.Delay(100);
    Check(sender.Health.SpoolPendingBatches==0 && sender.Health.ReplayedRequests==100 && sender.Health.RequestsSent==0,"restart replay counts weight without attributing old requests to new instance");
    Check(sender.Health.DetailsDropped>=0 && sender.Health.RequestsDropped==0,"aggregate recovery never creates negative detail loss");
}
Check(db.HeartbeatAggregates.Where(a=>a.ServiceName=="SpoolAggregate").Sum(a=>a.Count)==100,"recovered aggregate requests stored exactly once");
Check(db.Database.SqlQueryRaw<string>("PRAGMA integrity_check").AsEnumerable().Single()=="ok","SQLite integrity preserved");
var result=new {passed=true,checks,normalAggregated=aggregateCount,health=final,evidence=dbPath};
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);
}
