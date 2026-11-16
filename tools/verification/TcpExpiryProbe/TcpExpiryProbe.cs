using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// Local-only causal isolation: no game, Orleans, database, or APM dependencies.
var count = args.Length > 0 ? int.Parse(args[0]) : 30000;
if (count is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(count));
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(1024);
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var servers = new List<Task>();
var clients = new List<TcpClient>();
void Log(string phase) => Console.WriteLine(JsonSerializer.Serialize(new { phase, utc = DateTime.UtcNow, count, port, pid = Environment.ProcessId }));
async Task Receive(TcpClient client)
{
    using (client)
    {
        var buffer = new byte[1];
        while (await client.GetStream().ReadAsync(buffer, timeout.Token) != 0) { }
        client.Client.Shutdown(SocketShutdown.Send);
    }
}
var accept = Task.Run(async () =>
{
    for (var i = 0; i < count; i++) servers.Add(Receive(await listener.AcceptTcpClientAsync(timeout.Token)));
});
try
{
    Log("open-start");
    for (var i = 0; i < count; i++)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        clients.Add(client);
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        if ((i+1)%300==0) await Task.Delay(100,timeout.Token);
    }
    await accept;
    Log("close-start");
    foreach (var client in clients) client.Client.Shutdown(SocketShutdown.Send);
    await Task.WhenAll(servers);
    foreach (var client in clients) client.Dispose();
    listener.Stop();
    Log("closed");
    await Task.Delay(TimeSpan.FromSeconds(145),timeout.Token);
    Log("complete");
}
finally { foreach (var client in clients) client.Dispose();listener.Stop(); }
