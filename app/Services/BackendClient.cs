using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using DiametroLineaDesktop.Models;
namespace DiametroLineaDesktop.Services;
public class BackendClient
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _disconnectNotified;
    public event Action<string>? RawMessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(BackendSettings settings)
    {
        _cts?.Dispose();
        _ws?.Dispose();

        Interlocked.Exchange(ref _disconnectNotified, 0);
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
        NotifyDisconnectedOnce();
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
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var messageBuffer = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    var segment = new ArraySegment<byte>(buffer);
                    result = await ws.ReceiveAsync(segment, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    if (result.Count > 0)
                        messageBuffer.Write(buffer, 0, result.Count);

                } while (!result.EndOfMessage);

                var message = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                RawMessageReceived?.Invoke(message);
            }
        }
        catch (WebSocketException) { /* remote closed without handshake — normal for ESP32 */ }
        catch (OperationCanceledException) { /* disconnect requested */ }
        catch (IOException) { /* network reset */ }
        finally
        {
            NotifyDisconnectedOnce();
        }
    }

    private void NotifyDisconnectedOnce()
    {
        if (Interlocked.Exchange(ref _disconnectNotified, 1) == 0)
            Disconnected?.Invoke();
    }

    public static JsonDocument? TryParseJson(string text)
    {
        try { return JsonDocument.Parse(text); } catch { return null; }
    }
}
