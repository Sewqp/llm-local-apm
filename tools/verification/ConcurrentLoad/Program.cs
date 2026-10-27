using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var options = LoadOptions.Parse(args);
return await new LoadRun(options).RunAsync();

sealed record LoadOptions(int Count, int Rate, int HoldSeconds, int HeartbeatSeconds, int Port, string RunId, string Output)
{
    public static LoadOptions Parse(string[] args)
    {
        var values = args.Chunk(2).ToDictionary(a => a[0], a => a[1]);
        int Read(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value) : fallback;
        var result = new LoadOptions(Read("--count", 1000), Read("--rate", 500), Read("--hold", 15),
            Read("--heartbeat", 5), Read("--port", 9129), values["--run"], values["--output"]);
        if (result.Count is < 1 or > 30000 || result.Rate is < 1 or > 5000 || result.HoldSeconds is < 1 or > 180 ||
            result.HeartbeatSeconds is < 1 or > 10 || result.Port is < 1024 or > 65535) throw new ArgumentException("Invalid load bounds.");
        if (File.Exists(result.Output)) throw new IOException("Refusing to overwrite a previous result.");
        return result;
    }
}

sealed class LoadRun(LoadOptions options)
{
    private const ushort LoginRequest = 1000, LoginResult = 1001, Heartbeat = 9000;
    private readonly ConcurrentQueue<double> _connectMs = new(), _loginMs = new(), _heartbeatMs = new();
    private readonly ConcurrentDictionary<string, int> _errors = new();
    private readonly CancellationTokenSource _stopScheduling = new(), _abort = new(), _stopReporting = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly string _namePrefix = "cc_" + Guid.NewGuid().ToString("N")[..8];
    private int _attempted, _connected, _connectFailed, _loginOk, _loginFailed, _ready;
    private int _active, _activeLogged, _peakActive, _peakLogged, _unexpectedDisconnects;
    private int _heartbeatSent, _heartbeatAck, _holdMinimumLogged = int.MaxValue;
    private string _phase = "ramp";
    private double _holdStartedAt, _holdEndedAt;

    public async Task<int> RunAsync()
    {
        _abort.CancelAfter(TimeSpan.FromSeconds(options.Count / (double)options.Rate + options.HoldSeconds + 100));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _abort.Cancel(); };
        Console.WriteLine(JsonSerializer.Serialize(new { type = "configuration", options, timestamp = DateTime.UtcNow }));
        var reporting = ReportAsync();
        var clients = new List<Task>(options.Count);
        var chunk = Math.Max(1, options.Rate / 10);
        try
        {
            for (var index = 0; index < options.Count; index++)
            {
                _abort.Token.ThrowIfCancellationRequested();
                clients.Add(RunClientAsync(index));
                if ((index + 1) % chunk == 0) await Task.Delay(100, _abort.Token);
            }
            _phase = "awaiting_logins";
            while (Volatile.Read(ref _ready) < options.Count) await Task.Delay(100, _abort.Token);
            _holdStartedAt = _watch.Elapsed.TotalSeconds;
            _holdMinimumLogged = Volatile.Read(ref _activeLogged);
            _phase = "hold";
            await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds), _abort.Token);
            _holdEndedAt = _watch.Elapsed.TotalSeconds;
        }
        catch (OperationCanceledException) { _errors.AddOrUpdate("run_aborted", 1, (_, n) => n + 1); }
        finally
        {
            _phase = "drain";
            // Finish already sent heartbeats; a planned end must not look like an unacknowledged request.
            await _stopScheduling.CancelAsync();
            await Task.WhenAll(clients);
            await _stopReporting.CancelAsync();
            await reporting;
        }
        return WriteResult();
    }

    private async Task RunClientAsync(int index)
    {
        Interlocked.Increment(ref _attempted);
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        var connected = false; var logged = false; var ready = false;
        var stage = "connect";
        try
        {
            using (var timeout = RequestTimeout(15))
            {
                var start = Stopwatch.GetTimestamp();
                await client.ConnectAsync(IPAddress.Loopback, options.Port, timeout.Token);
                _connectMs.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            connected = true;
            Interlocked.Increment(ref _connected);
            UpdateMaximum(ref _peakActive, Interlocked.Increment(ref _active));
            stage = "login";
            await LoginAsync(client.GetStream(), index);
            logged = true;
            Interlocked.Increment(ref _loginOk);
            UpdateMaximum(ref _peakLogged, Interlocked.Increment(ref _activeLogged));
            ready = true; Interlocked.Increment(ref _ready);
            stage = "heartbeat";
            await HeartbeatsAsync(client.GetStream());
        }
        catch (Exception error)
        {
            if (!connected) Interlocked.Increment(ref _connectFailed);
            else if (!logged) Interlocked.Increment(ref _loginFailed);
            if (connected) Interlocked.Increment(ref _unexpectedDisconnects);
            var kind = error is SocketException socket ? socket.SocketErrorCode.ToString() : error.GetType().Name;
            _errors.AddOrUpdate(stage + ":" + kind, 1, (_, n) => n + 1);
        }
        finally
        {
            if (!ready) Interlocked.Increment(ref _ready);
            if (logged)
            {
                var current = Interlocked.Decrement(ref _activeLogged);
                if (_phase == "hold") UpdateMinimum(ref _holdMinimumLogged, current);
            }
            if (connected) Interlocked.Decrement(ref _active);
        }
    }

    private async Task LoginAsync(NetworkStream stream, int index)
    {
        using var timeout = RequestTimeout(40);
        var name = Encoding.UTF8.GetBytes($"{_namePrefix}_{index}");
        var payload = new byte[name.Length + 2];
        BitConverter.TryWriteBytes(payload.AsSpan(), (ushort)name.Length);
        name.CopyTo(payload.AsSpan(2));
        var start = Stopwatch.GetTimestamp();
        await stream.WriteAsync(Packet(LoginRequest, payload), timeout.Token);
        var response = await ReadAsync(stream, timeout.Token);
        if (response.Id != LoginResult || response.Payload.Length != 45 || response.Payload[0] != 1)
            throw new InvalidDataException("Unexpected login response.");
        _loginMs.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private async Task HeartbeatsAsync(NetworkStream stream)
    {
        using var scheduling = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token, _stopScheduling.Token);
        while (!scheduling.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.HeartbeatSeconds), scheduling.Token); }
            catch (OperationCanceledException) when (_stopScheduling.IsCancellationRequested && !_abort.IsCancellationRequested) { break; }
            using var timeout = RequestTimeout(15);
            var start = Stopwatch.GetTimestamp();
            await stream.WriteAsync(Packet(Heartbeat, []), timeout.Token);
            Interlocked.Increment(ref _heartbeatSent);
            var response = await ReadAsync(stream, timeout.Token);
            if (response.Id != Heartbeat) throw new InvalidDataException("Unexpected heartbeat response.");
            Interlocked.Increment(ref _heartbeatAck);
            _heartbeatMs.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private CancellationTokenSource RequestTimeout(int seconds)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return timeout;
    }

    private static byte[] Packet(ushort id, byte[] payload)
    {
        var bytes = new byte[payload.Length + 4];
        BitConverter.TryWriteBytes(bytes.AsSpan(), (ushort)bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), id);
        payload.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    private static async Task<(ushort Id, byte[] Payload)> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BitConverter.ToUInt16(header);
        if (size is < 4 or > 512) throw new InvalidDataException("Invalid packet length.");
        var payload = new byte[size - 4];
        await stream.ReadExactlyAsync(payload, ct);
        return (BitConverter.ToUInt16(header.AsSpan(2)), payload);
    }

    private async Task ReportAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(1000, _stopReporting.Token);
                if (_phase == "hold") UpdateMinimum(ref _holdMinimumLogged, Volatile.Read(ref _activeLogged));
                Console.WriteLine(JsonSerializer.Serialize(new { type = "progress", timestamp = DateTime.UtcNow,
                    seconds = Math.Round(_watch.Elapsed.TotalSeconds, 2), phase = _phase, attempted = _attempted,
                    active = _active, activeLogged = _activeLogged, peakActive = _peakActive, loginOk = _loginOk,
                    connectFailed = _connectFailed, loginFailed = _loginFailed, unexpectedDisconnects = _unexpectedDisconnects,
                    heartbeatSent = _heartbeatSent, heartbeatAck = _heartbeatAck }));
            }
        }
        catch (OperationCanceledException) { }
    }

    private int WriteResult()
    {
        var passed = _connected == options.Count && _loginOk == options.Count && _connectFailed == 0 &&
            _loginFailed == 0 && _unexpectedDisconnects == 0 && _holdMinimumLogged == options.Count &&
            _heartbeatSent == _heartbeatAck && _holdEndedAt - _holdStartedAt >= options.HoldSeconds && !_abort.IsCancellationRequested;
        var result = new { type = "summary", options, passed, completedAt = DateTime.UtcNow,
            totalSeconds = _watch.Elapsed.TotalSeconds, holdStartedSeconds = _holdStartedAt,
            holdEndedSeconds = _holdEndedAt, observedHoldSeconds = _holdEndedAt - _holdStartedAt,
            connected = _connected, connectFailed = _connectFailed, loginOk = _loginOk, loginFailed = _loginFailed,
            peakActive = _peakActive, peakLogged = _peakLogged, holdMinimumLogged = _holdMinimumLogged,
            unexpectedDisconnects = _unexpectedDisconnects, heartbeatSent = _heartbeatSent, heartbeatAck = _heartbeatAck,
            connectRttMs = Distribution(_connectMs), loginRttMs = Distribution(_loginMs), heartbeatRttMs = Distribution(_heartbeatMs), errors = _errors };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(options.Output, json);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return passed ? 0 : 2;
    }

    private static object Distribution(ConcurrentQueue<double> values)
    {
        var data = values.ToArray(); Array.Sort(data);
        double? Percentile(double p) => data.Length == 0 ? null : data[(int)Math.Ceiling(data.Length * p) - 1];
        return new { count = data.Length, p50 = Percentile(.50), p95 = Percentile(.95), p99 = Percentile(.99), max = Percentile(1) };
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int old;
        while (value > (old = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, old) != old) { }
    }
    private static void UpdateMinimum(ref int target, int value)
    {
        int old;
        while (value < (old = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, old) != old) { }
    }
}
