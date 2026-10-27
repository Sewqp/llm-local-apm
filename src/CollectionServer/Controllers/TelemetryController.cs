using Apm.Agent;
using CollectionServer.Data;
using CollectionServer.Models;
using CollectionServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController(ApmDbContext db) : ControllerBase
{
    [HttpPost("batch")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Ingest(TelemetryBatch batch, CancellationToken ct)
    {
        if (!Valid(batch)) return BadRequest(new { error = "Invalid telemetry batch; maximum 256 spans/metrics, 1024 logs, 256 runtime samples." });
        if (!await TelemetryBatchWriter.WriteAsync(db, batch, ct)) return Ok(new { duplicate = true });
        return Accepted(new { spans = batch.Spans.Count, metrics = batch.Metrics.Count });
    }

    private static bool Text(string? value, int max, bool optional = false) =>
        optional && value is null || !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool Hex(string? value, int length) => value?.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0');
    private static bool Time(DateTime value) => value.Kind == DateTimeKind.Utc &&
        value >= DateTime.UtcNow.AddDays(-7) && value <= DateTime.UtcNow.AddMinutes(5);
    private static bool Duration(double value) => double.IsFinite(value) && value >= 0 && value <= 86400000;
    private static bool Outcome(string? value) => value is "success" or "rejected" or "error" or "timeout" or "cancelled" or "unknown";

    private static bool Valid(TelemetryBatch batch)
    {
        if (!Hex(batch.BatchId, 32) || batch.Spans is null || batch.Metrics is null || batch.Logs is null || batch.Runtime is null ||
            batch.Spans.Count > 256 || batch.Metrics.Count > 256 || batch.Logs.Count > 1024 || batch.Runtime.Count > 256) return false;
        if (batch.Spans.Select(s => (s?.TraceId, s?.SpanId)).Distinct().Count() != batch.Spans.Count ||
            batch.Logs.Select(l => l?.Id).Distinct().Count() != batch.Logs.Count ||
            batch.Runtime.Select(r => r?.Id).Distinct().Count() != batch.Runtime.Count) return false;
        return batch.Spans.All(s => s is not null && Hex(s.TraceId, 32) && Hex(s.SpanId, 16) &&
                (s.ParentSpanId is null || Hex(s.ParentSpanId, 16)) && Text(s.ServiceName, 100) && Text(s.InstanceId, 100) &&
                Text(s.RunId, 100) && Text(s.Name, 160) && Text(s.Category, 40) && Text(s.CodeVersion, 200) &&
                Text(s.UserKey, 100, true) && Text(s.SessionId, 100, true) && Text(s.ResultCode, 160, true) &&
                Text(s.ErrorType, 160, true) && Text(s.DetailStatus, 40, true) && Text(s.SamplingReason, 40, true) &&
                Text(s.RequestMethod, 20, true) && Text(s.Route, 250, true) && Text(s.SourceFile, 250, true) &&
                (s.HttpStatusCode is null or >= 100 and <= 599) &&
                (s.LinkedTraceId is null && s.LinkedSpanId is null || Hex(s.LinkedTraceId, 32) && Hex(s.LinkedSpanId, 16)) &&
                s.ExpectedDetailSpans is >= 0 and <= 128 && s.LimitedDetailSpans >= 0 &&
                Time(s.StartTime) && Duration(s.DurationMs) && Outcome(s.Outcome)) &&
            batch.Metrics.All(m => m is not null && Text(m.ServerName, 100) && Text(m.Path, 250) && Text(m.Method, 20) &&
                Time(m.Timestamp) && Duration(m.DurationMs) && Hex(m.TraceId, 32) && Outcome(m.Outcome) &&
                Text(m.RunId, 100) && Text(m.UserKey, 100, true) && Text(m.SessionId, 100, true) && m.StatusCode is >= 100 and <= 599) &&
            batch.Logs.All(l => l is not null && Hex(l.Id, 32) && Hex(l.TraceId, 32) && Hex(l.SpanId, 16) && Time(l.Timestamp) &&
                Text(l.ServiceName, 100) && Text(l.Level, 20) && Text(l.Message, 2000) && Text(l.ErrorType, 160, true) && Text(l.StackTrace, 4000, true)) &&
            batch.Runtime.All(r => r is not null && Hex(r.Id, 32) && Time(r.Timestamp) && Text(r.ServiceName, 100) &&
                Text(r.InstanceId, 100) && Text(r.RunId, 100) && double.IsFinite(r.CpuPercent) && r.CpuPercent is >= 0 and <= 100 &&
                double.IsFinite(r.WorkingSetMb) && r.WorkingSetMb >= 0 && double.IsFinite(r.GcHeapMb) && r.GcHeapMb >= 0 &&
                r.ThreadPoolQueue >= 0 && r.ThreadPoolThreads >= 0 && r.ActiveSessions >= 0 && r.QueueDepth >= 0 &&
                r.Dropped >= 0 && r.FailedBatches >= 0 && r.Sent >= 0 && r.RequestQueueDepth >= 0 &&
                r.RequestsObserved >= 0 && r.RequestsSent >= 0 && r.RequestsDropped >= 0 && r.DetailsDropped >= 0 &&
                r.SampledOutTraces >= 0 && r.LimitedTraces >= 0 && r.UncorrelatedSpansSkipped >= 0 &&
                double.IsFinite(r.DetailSampleRate) && r.DetailSampleRate is >= 0 and <= 1);
    }
}
