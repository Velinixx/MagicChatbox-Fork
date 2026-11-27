using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using vrcosc_magicchatbox.Classes.DataAndSecurity;

namespace vrcosc_magicchatbox.Classes.Modules;

/// <summary>
/// Subscribes to a HypeRate channel over the Phoenix WebSocket protocol.
/// The watch runs the official HypeRate app, which publishes BPM to HypeRate's
/// cloud; this client only listens in — no BLE radio, no bridge process.
/// Same protocol as hr_bridge.py --hyperate: join "hr:&lt;user_id&gt;", relay
/// hr_update events (payload.hr), phoenix heartbeat every 10s.
/// </summary>
public sealed class HypeRateClient : IDisposable
{
    private const string UrlFormat = "wss://app.hyperate.io/socket/websocket?token={0}";
    private const int HeartbeatMs = 10_000; // Phoenix requires a keepalive every 10s
    private const int ReconnectMs = 5_000;

    public event Action<int>? HeartRateReceived;
    public event Action<bool>? ConnectionChanged;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _ref;
    private int _generation;
    private bool _connected;
    private DateTime _lastErrorLog = DateTime.MinValue;
    private bool _disposed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Start listening in the background. Connect failures retry forever.</summary>
    public void Start(string apiKey, string userId)
    {
        Stop();

        var generation = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => RunLoopAsync(generation, apiKey.Trim(), userId.Trim(), cts));
    }

    /// <summary>Stop listening. Safe to call when already stopped.</summary>
    public void Stop()
    {
        // Bump the generation first so any in-flight callback from the old
        // session becomes a no-op instead of racing the new one.
        Interlocked.Increment(ref _generation);
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        SetConnected(false);
    }

    private async Task RunLoopAsync(int generation, string apiKey, string userId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var url = string.Format(UrlFormat, Uri.EscapeDataString(apiKey));

        LogInfo($"connecting to channel hr:{userId}");

        while (!ct.IsCancellationRequested && generation == _generation)
        {
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct);
                if (generation != _generation) break; // stopped while connecting

                _ws = ws;
                SetConnected(true);
                LogInfo("connected");

                await SendAsync(ws, $"hr:{userId}", "phx_join", new JObject(), ct);

                // Keep this connection alive until it drops or we stop.
                var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = HeartbeatLoopAsync(ws, linkCts.Token);
                try
                {
                    await ReceiveLoopAsync(ws, generation, linkCts.Token);
                }
                finally
                {
                    linkCts.Cancel();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_ws, ws)) _ws = null;
                try { ws?.Dispose(); } catch { }
            }

            if (ct.IsCancellationRequested || generation != _generation) break;

            LogError($"connection lost — retrying in {ReconnectMs / 1000}s");
            try { await Task.Delay(ReconnectMs, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }
        }

        if (generation == _generation)
        {
            _cts = null;
            SetConnected(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, int generation, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var message = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                LogInfo($"server closed the connection ({result.CloseStatus})");
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text) continue;

            message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var raw = message.ToString();
            message.Clear();

            HandleMessage(raw, generation);
        }
    }

    private void HandleMessage(string raw, int generation)
    {
        try
        {
            var msg = JObject.Parse(raw);
            if (generation != _generation) return;

            // Ignore phx_reply / phx_error / heartbeat acks — only BPM matters.
            if (msg.Value<string>("event") != "hr_update") return;

            var bpm = msg["payload"]?["hr"]?.Value<int?>() ?? 0;
            if (bpm < 20 || bpm > 250) return; // 0 = sensor lost contact

            HeartRateReceived?.Invoke(bpm);
        }
        catch (Exception ex)
        {
            LogError($"bad message: {ex.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(HeartbeatMs, ct);
                await SendAsync(ws, "phoenix", "heartbeat", new JObject(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogError(ex.Message); }
    }

    private async Task SendAsync(ClientWebSocket ws, string topic, string eventName, JObject payload, CancellationToken ct)
    {
        var json = JsonConvert.SerializeObject(new
        {
            topic,
            @event = eventName,
            payload,
            @ref = NextRef(),
        });

        var bytes = Encoding.UTF8.GetBytes(json);

        // ClientWebSocket forbids concurrent sends — serialize joins and heartbeats.
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private int NextRef() => Interlocked.Increment(ref _ref);

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private static void LogInfo(string message) => Logging.WriteInfo($"C20 HypeRate: {message}");

    private void LogError(string message)
    {
        // The reconnect loop can fail every 5s — keep the log readable.
        if ((DateTime.Now - _lastErrorLog).TotalSeconds < 30) return;
        _lastErrorLog = DateTime.Now;
        Logging.WriteInfo($"C20 HypeRate: {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
