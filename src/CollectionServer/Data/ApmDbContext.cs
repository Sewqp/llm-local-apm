using CollectionServer.Models;
using Microsoft.EntityFrameworkCore;

namespace CollectionServer.Data;

public class ApmDbContext(DbContextOptions<ApmDbContext> options) : DbContext(options)
{
    public DbSet<MetricRecord> Metrics => Set<MetricRecord>();
    public DbSet<AnalysisResult> Analyses => Set<AnalysisResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetricRecord>(e =>
        {
            e.HasIndex(m => new { m.Path, m.Timestamp });
        });
    }
}
