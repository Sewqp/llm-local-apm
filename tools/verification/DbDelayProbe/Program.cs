using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

// This fixture is intentionally tied to the isolated validation database/port.
await using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=15439;Database=apm_verification;Username=apmtest");
await connection.OpenAsync();
await using var transaction = await connection.BeginTransactionAsync();
await using var command = new NpgsqlCommand("LOCK TABLE player IN ACCESS EXCLUSIVE MODE", connection, transaction);
await command.ExecuteNonQueryAsync();
using var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 9129);
var name = Encoding.UTF8.GetBytes("delay_" + Guid.NewGuid().ToString("N")[..12]);
var packet = new byte[6 + name.Length];
BitConverter.TryWriteBytes(packet.AsSpan(), (ushort)packet.Length);
BitConverter.TryWriteBytes(packet.AsSpan(2), (ushort)1000);
BitConverter.TryWriteBytes(packet.AsSpan(4), (ushort)name.Length);
name.CopyTo(packet.AsSpan(6));
var watch = Stopwatch.StartNew();
await client.GetStream().WriteAsync(packet);
await Task.Delay(800);
await transaction.CommitAsync();
var header = new byte[4];
await client.GetStream().ReadExactlyAsync(header);
var payload = new byte[BitConverter.ToUInt16(header) - 4];
await client.GetStream().ReadExactlyAsync(payload);
var roundTrip = watch.Elapsed.TotalMilliseconds;
if (payload[0] != 1) throw new Exception("Login did not succeed after lock release");
await Task.Delay(1500);
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5229") };
var page = await http.GetFromJsonAsync<JsonElement>("/api/traces?server=Trickcal-APM-Verification&operation=LoginRequest");
var root = page.GetProperty("items")[0];
var traceId = root.GetProperty("traceId").GetString();
var detail = await http.GetFromJsonAsync<JsonElement>("/api/traces/" + traceId);
var slowestDb = detail.GetProperty("spans").EnumerateArray().Where(s => s.GetProperty("category").GetString() == "database")
    .Max(s => s.GetProperty("durationMs").GetDouble());
if (slowestDb < 600) throw new Exception($"Injected DB wait not visible: {slowestDb}");
Console.WriteLine(JsonSerializer.Serialize(new
{
    scenario = "deliberately injected PostgreSQL table-lock wait", passed = true, traceId,
    heldLockMs = 800, clientRoundTripMs = roundTrip, observedDatabaseSpanMs = slowestDb,
    rootDurationMs = root.GetProperty("durationMs").GetDouble()
}, new JsonSerializerOptions { WriteIndented = true }));
