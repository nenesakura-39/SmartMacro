using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SmartMacro.Core.Storage;

/// <summary>Derives deterministic per-user Named Pipe names for single-instance CLI IPC.</summary>
public static class CliIpcNames
{
    public static string GetDefaultPipeName() => GetPipeName(scopeOverride: null);

    public static string GetPipeName(string? scopeOverride)
    {
        var identity = ResolveCurrentUserIdentity();
        var raw = string.IsNullOrWhiteSpace(scopeOverride)
            ? identity
            : $"{identity}|{scopeOverride.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var suffix = Convert.ToHexString(hash[..8]).ToLowerInvariant();
        return $"SmartMacro.Ipc.{suffix}";
    }

    private static string ResolveCurrentUserIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                if (!string.IsNullOrWhiteSpace(id.User?.Value))
                    return id.User.Value;
            }
            catch
            {
                // Fall back to user/domain name below if token lookup fails.
            }
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }
}

internal static class CliIpcFraming
{
    internal const int MaxFrameBytes = 256 * 1024;

    public static async Task WriteFrameAsync<T>(Stream stream, T payload, CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, MacroJson.Options);
        if (jsonBytes.Length > MaxFrameBytes)
            throw new InvalidOperationException($"IPC payload exceeded maximum frame size ({MaxFrameBytes} bytes).");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, jsonBytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Invalid IPC frame length: {length}.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, MacroJson.Options);
    }
}

/// <summary>
/// Per-user authenticated Named Pipe server running inside the primary SmartMacro instance.
/// Accepts multiple concurrent clients so <c>--stop</c> can execute immediately even while another client waits on <c>--wait</c>.
/// </summary>
public sealed class CliIpcServer : IAsyncDisposable, IDisposable
{
    private readonly string _pipeName;
    private readonly Func<CliCommandRequest, CancellationToken, Task<CliCommandResponse>> _handler;
    private readonly Func<CliCommandRequest, CliCommandResponse, Task>? _afterResponseSent;
    private readonly Action<string, Exception?>? _logError;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private readonly Task _acceptLoopTask;
    private long _connectionId;
    private int _disposed;

    public CliIpcServer(
        string pipeName,
        Func<CliCommandRequest, CancellationToken, Task<CliCommandResponse>> handler,
        Func<CliCommandRequest, CliCommandResponse, Task>? afterResponseSent = null,
        Action<string, Exception?>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));

        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _afterResponseSent = afterResponseSent;
        _logError = logError;
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public string PipeName => _pipeName;

    private NamedPipeServerStream CreateServerStream() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = server;
                server = null;

                var id = Interlocked.Increment(ref _connectionId);
                var task = HandleClientAsync(connected, ct);
                _inFlight[id] = task;
                _ = task.ContinueWith(_ => _inFlight.TryRemove(id, out Task? _), TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logError?.Invoke("CLI IPC accept loop error.", ex);
                try { await Task.Delay(50, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (server is not null)
                    await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                var request = await CliIpcFraming.ReadFrameAsync<CliCommandRequest>(stream, ct).ConfigureAwait(false);
                if (request is null) return;

                var response = await _handler(request, ct).ConfigureAwait(false);
                await CliIpcFraming.WriteFrameAsync(stream, response, ct).ConfigureAwait(false);

                if (_afterResponseSent is not null)
                {
                    using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    drainCts.CancelAfter(250);
                    try
                    {
                        var ack = new byte[1];
                        _ = await stream.ReadAsync(ack, drainCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Client already closed or drain window elapsed; proceed safely.
                    }

                    await _afterResponseSent(request, response).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Server is shutting down.
            }
            catch (IOException)
            {
                // Client disconnected before reading the reply.
            }
            catch (Exception ex)
            {
                _logError?.Invoke("CLI IPC request handling failed.", ex);
                try
                {
                    await CliIpcFraming.WriteFrameAsync(
                        stream,
                        new CliCommandResponse(CliExitCodes.BusyOrIpcError, "Error", ex.Message),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Pipe already closed.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _acceptLoopTask.WaitAsync(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown timeout or cancellation.
        }

        var active = _inFlight.Values.ToArray();
        if (active.Length > 0)
        {
            try
            {
                await Task.WhenAll(active).WaitAsync(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore remaining tasks on shutdown.
            }
        }

        _cts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

/// <summary>Client helper used by secondary SmartMacro launches to forward CLI requests to the running instance.</summary>
public static class CliIpcClient
{
    public static async Task<CliCommandResponse?> SendAsync(
        string pipeName,
        CliCommandRequest request,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        ArgumentNullException.ThrowIfNull(request);

        var deadlineMs = Environment.TickCount64 + Math.Max(100, (long)connectTimeout.TotalMilliseconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remainingMs = (int)Math.Clamp(deadlineMs - Environment.TickCount64, 1, 250);

            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await client.ConnectAsync(remainingMs, ct).ConfigureAwait(false);
                using var responseCts = CreateTimeoutCts(responseTimeout, ct);
                var ioToken = responseCts?.Token ?? ct;

                await CliIpcFraming.WriteFrameAsync(client, request, ioToken).ConfigureAwait(false);
                return await CliIpcFraming.ReadFrameAsync<CliCommandResponse>(client, ioToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or FileNotFoundException && !ct.IsCancellationRequested)
            {
                if (Environment.TickCount64 >= deadlineMs)
                    return null;

                await Task.Delay(35, ct).ConfigureAwait(false);
            }
        }
    }

    private static CancellationTokenSource? CreateTimeoutCts(TimeSpan timeout, CancellationToken parent)
    {
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(timeout);
        return cts;
    }
}
