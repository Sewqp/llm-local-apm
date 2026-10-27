using Apm.Agent;
using CollectionServer.Models;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Data;

public class ApmDbContext(DbContextOptions<ApmDbContext> options) : DbContext(options)
{
    public DbSet<MetricRecord> Metrics => Set<MetricRecord>();
    public DbSet<AnalysisResult> Analyses => Set<AnalysisResult>();

    public DbSet<SpanData> Spans => Set<SpanData>();
    public DbSet<LogData> TelemetryLogs => Set<LogData>();
    public DbSet<RuntimeData> RuntimeSamples => Set<RuntimeData>();
    public DbSet<TelemetryBatchReceipt> TelemetryBatches => Set<TelemetryBatchReceipt>();
    public DbSet<TraceInvestigation> TraceInvestigations => Set<TraceInvestigation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpanData>().HasKey(s => new { s.TraceId, s.SpanId });
        modelBuilder.Entity<SpanData>().HasIndex(s => new { s.ServiceName, s.StartTime });
        modelBuilder.Entity<SpanData>().HasIndex(s => new { s.IsEntry, s.StartTime });
        modelBuilder.Entity<LogData>().HasIndex(s => new { s.TraceId, s.Timestamp });
        modelBuilder.Entity<RuntimeData>().HasIndex(s => new { s.ServiceName, s.Timestamp });
        modelBuilder.Entity<TelemetryBatchReceipt>().ToTable("TelemetryBatches");
        modelBuilder.Entity<MetricRecord>(e =>
        {
            e.HasIndex(m => new { m.Path, m.Timestamp });
        });
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
                property.SetValueConverter(utc);
    }
}
