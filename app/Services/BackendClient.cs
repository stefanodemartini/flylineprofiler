using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DiametroLineaDesktop.Models;
namespace DiametroLineaDesktop.Services;
public class BackendClient
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    public event Action<string>? RawMessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(BackendSettings settings)
    {
        _cts?.Dispose();
        _ws?.Dispose();

        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds));
        _ws = new ClientWebSocket();

        var uri = new Uri($"ws://{settings.Host}:{settings.WebSocketPort}/");

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);
            Connected?.Invoke();
            _ = ReceiveLoopAsync(_ws, CancellationToken.None);
        }
        catch
        {
            _ws.Dispose();
            _ws = null;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_ws is { State: WebSocketState.Open })
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        Disconnected?.Invoke();
    }

    public async Task SendAsync(string command)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(command);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task<string?> FetchExportCsvAsync(BackendSettings settings)
    {
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"http://{settings.Host}:{settings.HttpPort}/export";
        try
        {
            return await client.GetStringAsync(url);
        }
        catch
        {
            return null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var segment = new ArraySegment<byte>(buffer);
            var result = await ws.ReceiveAsync(segment, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            RawMessageReceived?.Invoke(message);
        }
        Disconnected?.Invoke();
    }

    public static JsonDocument? TryParseJson(string text)
    {
        try { return JsonDocument.Parse(text); } catch { return null; }
    }
}
