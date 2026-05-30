using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Ducker.Ipc;

/// <summary>
/// Minimal loopback WebSocket server that pushes DIALOG_START / DIALOG_END events to the
/// browser extension. Binds to 127.0.0.1 only and requires a shared token in the query
/// string (ws://127.0.0.1:PORT/?token=...). Sends a PING every 15 s so the extension's
/// MV3 service worker stays alive and connections are kept warm.
/// </summary>
public sealed class DialogWebSocketServer : IDisposable
{
    private sealed class Client
    {
        public WebSocket Socket;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    private readonly HttpListener _listener = new();
    private readonly string _token;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer _pingTimer;

    public DialogWebSocketServer(int port, string token)
    {
        _token = token;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
        _pingTimer = new System.Threading.Timer(_ => Broadcast("{\"type\":\"PING\"}"), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener stopped

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 426; // Upgrade Required
                ctx.Response.Close();
                continue;
            }
            if (ctx.Request.QueryString["token"] != _token)
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                Console.WriteLine("[ws] rejected connection: bad token");
                continue;
            }

            WebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
            catch { continue; }

            var id = Guid.NewGuid();
            var client = new Client { Socket = wsCtx.WebSocket };
            _clients[id] = client;
            Console.WriteLine($"[ws] extension connected ({_clients.Count} client(s))");
            _ = Task.Run(() => ReceiveLoop(id, client));
        }
    }

    private async Task ReceiveLoop(Guid id, Client client)
    {
        var buf = new byte[1024];
        try
        {
            while (client.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await client.Socket.ReceiveAsync(buf, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                // Incoming messages (e.g. client keepalive/hello) are ignored for the spike.
            }
        }
        catch { /* connection dropped */ }
        finally
        {
            _clients.TryRemove(id, out _);
            try { await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            Console.WriteLine($"[ws] extension disconnected ({_clients.Count} client(s))");
        }
    }

    public void Broadcast(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var kv in _clients)
            _ = SendTo(kv.Key, kv.Value, bytes);
    }

    private async Task SendTo(Guid id, Client client, byte[] bytes)
    {
        await client.SendLock.WaitAsync();
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { _clients.TryRemove(id, out _); }
        finally { client.SendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pingTimer?.Dispose();
        try { _listener.Stop(); } catch { }
        foreach (var kv in _clients)
            try { kv.Value.Socket.Abort(); } catch { }
    }
}
