using Apm.Agent;
using Microsoft.Data.Sqlite;

namespace ArchitectureDemo;

public sealed class CatalogStore
{
    private readonly string _connectionString;
    public CatalogStore(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new InvalidOperationException("Use a new fixture database; existing data is never reset.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE Catalog (Kind TEXT PRIMARY KEY, Value INTEGER NOT NULL);
            INSERT INTO Catalog VALUES ('inventory',100), ('price',2500), ('eligibility',1);
            CREATE TABLE Rewards (JobId TEXT PRIMARY KEY, Amount INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
    public async Task<int> ReadAsync(string kind, int simulatedDelayMs)
    {
        using var operation = LocalTelemetry.StartSpan($"catalog.{kind}");
        // Simulated dependency latency is separate from the measured, real SQLite query.
        using (LocalTelemetry.StartSpan($"simulation.{kind}.wait", "wait")) await Task.Delay(simulatedDelayMs);
        using var query = LocalTelemetry.StartSpan("SQLite.catalog.select", "database");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Catalog WHERE Kind = $kind";
        command.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public void Grant(string jobId)
    {
        using var span = LocalTelemetry.StartSpan("SQLite.reward.insert", "database");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Rewards (JobId, Amount) VALUES ($id,100) ON CONFLICT(JobId) DO NOTHING";
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }
}
