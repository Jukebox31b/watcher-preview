using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class EdgeCdpClient
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private int _nextCommandId;

    public async Task<bool> IsReachableAsync(AppConfig config, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(GetEndpoint(config, "/json/version"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetVersionAsync(AppConfig config, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(GetEndpoint(config, "/json/version"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EdgeCdpTarget>> ListTargetsAsync(AppConfig config, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(GetEndpoint(config, "/json/list"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var targets = await JsonSerializer.DeserializeAsync<List<EdgeCdpTarget>>(stream, cancellationToken: cancellationToken);
        return targets ?? [];
    }

    public async Task<EdgeCdpTarget?> OpenTargetAsync(AppConfig config, string url, CancellationToken cancellationToken)
    {
        var endpoint = GetEndpoint(config, "/json/new?" + Uri.EscapeDataString(url));
        using var response = await _httpClient.PutAsync(endpoint, content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<EdgeCdpTarget>(stream, cancellationToken: cancellationToken);
    }

    public async Task BringToFrontAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            return;
        }

        await SendCommandAsync(target, "Page.bringToFront", parameters: null, cancellationToken);
    }

    public async Task ReloadAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            return;
        }

        await SendCommandAsync(
            target,
            "Page.reload",
            new { ignoreCache = true },
            cancellationToken);
    }

    public async Task<JsonElement?> EvaluateAsync(
        EdgeCdpTarget target,
        string expression,
        CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            target,
            "Runtime.evaluate",
            new
            {
                expression,
                awaitPromise = true,
                returnByValue = true
            },
            cancellationToken);

        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.ToString());
        }

        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("result", out var runtimeResult))
        {
            return null;
        }

        if (runtimeResult.TryGetProperty("value", out var value))
        {
            return value.Clone();
        }

        if (runtimeResult.TryGetProperty("description", out var description))
        {
            return JsonSerializer.SerializeToElement(description.GetString() ?? string.Empty);
        }

        return null;
    }

    private async Task<JsonElement> SendCommandAsync(
        EdgeCdpTarget target,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException("CDP target does not expose a WebSocket debugger URL.");
        }

        using var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), cancellationToken);

        var id = Interlocked.Increment(ref _nextCommandId);
        var command = parameters is null
            ? new Dictionary<string, object?> { ["id"] = id, ["method"] = method }
            : new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters };
        var payload = JsonSerializer.SerializeToUtf8Bytes(command);
        await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        var buffer = new byte[64 * 1024];
        while (webSocket.State == WebSocketState.Open)
        {
            using var memory = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("CDP WebSocket closed before command completed.");
                }

                memory.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(memory.ToArray());
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                return root.Clone();
            }
        }

        throw new InvalidOperationException("CDP WebSocket closed before a matching response arrived.");
    }

    private static string GetEndpoint(AppConfig config, string path)
    {
        var host = string.IsNullOrWhiteSpace(config.ChatGptCdpHost) ? "127.0.0.1" : config.ChatGptCdpHost.Trim();
        return $"http://{host}:{config.ChatGptCdpPort}{path}";
    }
}

public sealed class EdgeCdpTarget
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("webSocketDebuggerUrl")]
    public string WebSocketDebuggerUrl { get; set; } = string.Empty;
}
