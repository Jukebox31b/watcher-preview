using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record CodexIpcResult(
    bool Confirmed,
    bool Connected,
    bool RequestSent,
    bool PipeUnavailable,
    string Message,
    string? TurnId = null,
    string? Error = null,
    bool SafeToRetry = false);

public sealed class CodexIpcClient
{
    public const string DefaultPipeName = "\\\\" + @".\pipe\codex-ipc";
    public const string StartTurnMethod = "thread-follower-start-turn";
    private const int MaxFrameBytes = 50_000_000;

    public async Task<CodexIpcResult> StartTurnAsync(
        AppConfig config,
        string prompt,
        LogService log,
        CancellationToken cancellationToken = default)
    {
        var threadId = config.CodexThreadId.Trim();
        var configuredPipeName = string.IsNullOrWhiteSpace(config.CodexIpcPipeName)
            ? DefaultPipeName
            : config.CodexIpcPipeName.Trim();

        log.Info($"IPC pipe name: {configuredPipeName}", "CodexIPC");
        log.Info(string.IsNullOrWhiteSpace(threadId)
            ? "Codex thread ID missing."
            : $"Codex thread ID: {threadId}", "CodexIPC");

        if (string.IsNullOrWhiteSpace(threadId))
        {
            const string message = "DELIVERY_FAILED: CodexThreadId is required for IPC delivery.";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, false, false, false, message, Error: message);
        }

        var pipeAddress = ParsePipeAddress(configuredPipeName);
        log.Info($"IPC connect attempt: server={pipeAddress.ServerName} pipe={pipeAddress.PipeName}", "CodexIPC");

        using var pipe = new NamedPipeClientStream(
            pipeAddress.ServerName,
            pipeAddress.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.CodexIpcConnectTimeoutSeconds)));
            await pipe.ConnectAsync(connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var message = "Codex IPC pipe unavailable or connect timed out. Start Codex and open the Director thread once.";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, false, false, true, message, Error: message);
        }
        catch (IOException ex)
        {
            var message = $"Codex IPC pipe connection failed: {ex.Message}";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, false, false, true, message, Error: ex.Message);
        }
        catch (Exception ex)
        {
            var message = $"Codex IPC connect failed: {ex.Message}";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, false, false, true, message, Error: ex.Message);
        }

        log.Info("IPC connected.", "CodexIPC");

        try
        {
            var initRequestId = Guid.NewGuid().ToString("N");
            await WriteCodexIpcFrameAsync(pipe, new
            {
                type = "request",
                requestId = initRequestId,
                sourceClientId = "initializing-client",
                version = 0,
                method = "initialize",
                @params = new
                {
                    clientType = "dcs-watcher-v2"
                }
            }, cancellationToken);
            log.Info("IPC initialize request sent.", "CodexIPC");

            var initResponse = await ReadCodexIpcResponseAsync(
                pipe,
                initRequestId,
                TimeSpan.FromSeconds(Math.Max(1, config.CodexIpcResponseTimeoutSeconds)),
                cancellationToken);
            if (!IsCodexIpcSuccess(initResponse))
            {
                var error = DescribeCodexIpcError(initResponse);
                var message = $"Codex IPC initialize failed: {error}";
                log.Error(message, "CodexIPC");
                return new CodexIpcResult(false, true, false, false, message, Error: error);
            }

            var clientId = initResponse["result"]?["clientId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                const string message = "Codex IPC initialize response did not include a client id.";
                log.Error(message, "CodexIPC");
                return new CodexIpcResult(false, true, false, false, message, Error: message);
            }

            var startRequestId = Guid.NewGuid().ToString("N");
            var timeoutMs = Math.Clamp(config.CodexIpcResponseTimeoutSeconds, 1, 300) * 1000;
            await WriteCodexIpcFrameAsync(pipe, new
            {
                type = "request",
                requestId = startRequestId,
                sourceClientId = clientId,
                version = 1,
                method = StartTurnMethod,
                @params = new
                {
                    conversationId = threadId,
                    turnStartParams = new
                    {
                        input = new[]
                        {
                            new
                            {
                                type = "text",
                                text = prompt,
                                text_elements = Array.Empty<object>()
                            }
                        },
                        serviceTier = (string?)null
                    }
                },
                timeoutMs
            }, cancellationToken);
            log.Info($"IPC request sent: {StartTurnMethod}.", "CodexIPC");

            JsonObject startResponse;
            try
            {
                startResponse = await ReadCodexIpcResponseAsync(
                    pipe,
                    startRequestId,
                    TimeSpan.FromMilliseconds(timeoutMs),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                const string message = "Codex IPC start-turn response timed out.";
                log.Error(message, "CodexIPC");
                return new CodexIpcResult(false, true, true, false, message, Error: message);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Invalid Codex IPC frame length:", StringComparison.Ordinal))
            {
                var message = $"Codex IPC response frame was invalid: {ex.Message}";
                log.Error(message, "CodexIPC");
                return new CodexIpcResult(false, true, true, false, message, Error: ex.Message);
            }
            catch (EndOfStreamException ex)
            {
                var message = $"Codex IPC pipe closed before confirming turn start: {ex.Message}";
                log.Error(message, "CodexIPC");
                return new CodexIpcResult(false, true, true, false, message, Error: ex.Message);
            }

            if (!IsCodexIpcSuccess(startResponse))
            {
                var error = DescribeCodexIpcError(startResponse);
                var message = error.Contains("no-client-found", StringComparison.OrdinalIgnoreCase)
                    ? $"Codex IPC could not find an owner for thread {threadId}. Open that Codex Director thread once, then retry."
                    : $"Codex IPC start turn failed: {error}";
                log.Error(message, "CodexIPC");
                // A structured failure response proves that Codex rejected the request and did not start a turn.
                return new CodexIpcResult(false, true, true, false, message, Error: error, SafeToRetry: true);
            }

            var turnId =
                startResponse["result"]?["result"]?["turn"]?["id"]?.GetValue<string>() ??
                startResponse["result"]?["turn"]?["id"]?.GetValue<string>() ??
                "(unknown turn id)";
            var confirmedMessage = $"IPC confirmed turn start for thread {threadId}; turn {turnId}.";
            log.Info(confirmedMessage, "CodexIPC");
            return new CodexIpcResult(true, true, true, false, confirmedMessage, turnId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string message = "Codex IPC delivery cancelled.";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, pipe.IsConnected, false, false, message, Error: message);
        }
        catch (OperationCanceledException)
        {
            const string message = "Codex IPC operation timed out.";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, pipe.IsConnected, false, false, message, Error: message);
        }
        catch (Exception ex)
        {
            var message = $"Codex IPC delivery failed: {ex.Message}";
            log.Error(message, "CodexIPC");
            return new CodexIpcResult(false, pipe.IsConnected, false, false, message, Error: ex.Message);
        }
    }

    public static PipeAddress ParsePipeAddress(string configuredPipeName)
    {
        var value = string.IsNullOrWhiteSpace(configuredPipeName)
            ? DefaultPipeName
            : configuredPipeName.Trim().Replace('/', '\\');

        const string localPrefix = "\\\\" + @".\pipe\";
        if (value.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new PipeAddress(".", value[localPrefix.Length..]);
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = value[2..].Split('\\', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase))
            {
                var serverName = parts[0].Equals(".", StringComparison.Ordinal) ||
                                 parts[0].Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    ? "."
                    : parts[0];
                return new PipeAddress(serverName, parts[2]);
            }
        }

        return new PipeAddress(".", value.TrimStart('\\'));
    }

    private static async Task WriteCodexIpcFrameAsync(Stream stream, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(payload.Length);

        await stream.WriteAsync(length.AsMemory(0, length.Length), cancellationToken);
        await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<JsonObject> ReadCodexIpcResponseAsync(
        Stream stream,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            var frame = await ReadCodexIpcFrameAsync(stream, timeoutCts.Token);
            if (frame is not JsonObject response)
            {
                continue;
            }

            if (GetString(response, "type").Equals("response", StringComparison.OrdinalIgnoreCase) &&
                GetString(response, "requestId").Equals(requestId, StringComparison.Ordinal))
            {
                return response;
            }
        }
    }

    private static async Task<JsonNode?> ReadCodexIpcFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Invalid Codex IPC frame length: {length}");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonNode.Parse(Encoding.UTF8.GetString(payload));
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Codex IPC pipe closed while reading a frame");
            }

            offset += read;
        }
    }

    private static bool IsCodexIpcSuccess(JsonObject response)
    {
        return GetString(response, "resultType").Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeCodexIpcError(JsonObject response)
    {
        var error = response["error"]?.ToJsonString();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        var resultType = GetString(response, "resultType");
        return string.IsNullOrWhiteSpace(resultType) ? response.ToJsonString() : resultType;
    }

    private static string GetString(JsonNode? node, string propertyName)
    {
        return node?[propertyName]?.GetValue<string>() ?? string.Empty;
    }
}

public sealed record PipeAddress(string ServerName, string PipeName);
