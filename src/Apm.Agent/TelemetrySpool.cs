using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Apm.Agent;

internal sealed class TelemetrySpool : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly long _maxBytes;
    private readonly int _maxBatches;
    private readonly SortedDictionary<string, (long Bytes, int Requests)> _pending = new(StringComparer.Ordinal);
    private long _bytes, _requests;

    public TelemetrySpool(AgentOptions options)
    {
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(options.ServiceName + "|" + options.Endpoint)))[..24];
        _directory = Path.Combine(Path.GetFullPath(options.SpoolDirectory!), identity);
        _maxBytes = options.MaxSpoolBytes; _maxBatches = options.MaxSpoolBatches;
        Directory.CreateDirectory(_directory);
        // A second writer must use a different configured directory, never race acknowledgement/deletion.
        _lease = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                var batch = Read(path);
                var size = new FileInfo(path).Length;
                _pending.Add(path, (size, batch.Metrics.Count));
                _bytes += size; _requests += batch.Metrics.Count;
            }
            // Only our incomplete atomic-write files are removed; committed JSON files are never reset.
            foreach (var path in Directory.EnumerateFiles(_directory, "*.tmp")) File.Delete(path);
        }
        catch { _lease.Dispose(); throw; }
    }

    public (long Batches, long Requests, long Bytes) Health
    {
        get { lock (_gate) return (_pending.Count, _requests, _bytes); }
    }

    public bool TryStore(TelemetryBatch batch)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(batch, Json);
        lock (_gate)
        {
            if (_pending.Count >= _maxBatches || data.Length > _maxBytes - _bytes) return false;
            var path = Path.Combine(_directory, DateTime.UtcNow.Ticks.ToString("D19") + "-" + batch.BatchId + ".json");
            var temporary = path + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(data);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
                _pending.Add(path, (data.Length, batch.Metrics.Count));
                _bytes += data.Length; _requests += batch.Metrics.Count;
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public (string Path, TelemetryBatch Batch)? Peek()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var path = _pending.First().Key;
            return (path, Read(path));
        }
    }

    public void Acknowledge(string path)
    {
        lock (_gate)
        {
            var entry = _pending[path];
            File.Delete(path);
            _pending.Remove(path);
            _bytes -= entry.Bytes; _requests -= entry.Requests;
        }
    }

    private static TelemetryBatch Read(string path) =>
        JsonSerializer.Deserialize<TelemetryBatch>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Invalid telemetry spool batch: " + path);

    public void Dispose() => _lease.Dispose();
}
