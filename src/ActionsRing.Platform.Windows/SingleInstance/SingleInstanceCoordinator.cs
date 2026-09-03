using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ActionsRing.Platform.Windows.SingleInstance;

public sealed record InstanceActivationRequest(
    string[] Arguments,
    string WorkingDirectory,
    DateTimeOffset RequestedAtUtc,
    string? Payload = null)
{
    public static InstanceActivationRequest FromCurrentProcess(string? payload = null) =>
        new(Environment.GetCommandLineArgs().Skip(1).ToArray(), Environment.CurrentDirectory, DateTimeOffset.UtcNow, payload);
}

public sealed class InstanceActivationEventArgs : EventArgs
{
    public InstanceActivationEventArgs(InstanceActivationRequest request) => Request = request;
    public InstanceActivationRequest Request { get; }
}

/// <summary>
/// Keeps one application instance per interactive Windows session and forwards
/// subsequent launches to the primary instance over a current-user-only named pipe.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable, IAsyncDisposable
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly Mutex _lifetimeMutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _listenerGate = new();
    private Task? _listenerTask;
    private int _disposed;

    private SingleInstanceCoordinator(Mutex lifetimeMutex, string pipeName, bool isPrimary)
    {
        _lifetimeMutex = lifetimeMutex;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public event EventHandler<InstanceActivationEventArgs>? ActivationReceived;
    public event EventHandler<PlatformErrorEventArgs>? PlatformError;

    public bool IsPrimary { get; }

    public static SingleInstanceCoordinator Acquire(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId)))[..32];
        var mutex = new Mutex(initiallyOwned: false, $@"Local\ActionsRing.{hash}", out var createdNew);
        return new SingleInstanceCoordinator(mutex, $"ActionsRing.{hash}", createdNew);
    }

    /// <summary>
    /// Starts the primary instance listener. It is safe to call more than once.
    /// </summary>
    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activations.");
        }

        lock (_listenerGate)
        {
            _listenerTask ??= Task.Run(ListenLoopAsync);
        }
    }

    public async Task NotifyPrimaryAsync(
        InstanceActivationRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        var json = JsonSerializer.SerializeToUtf8Bytes(request);
        if (json.Length > MaximumMessageBytes)
        {
            throw new ArgumentException("The activation payload is too large.", nameof(request));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

        await using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
            await pipe.WriteAsync(header, timeoutSource.Token).ConfigureAwait(false);
            await pipe.WriteAsync(json, timeoutSource.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The primary Actions Ring instance did not accept activation in time.");
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                messageTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await ReadRequestAsync(pipe, messageTimeout.Token).ConfigureAwait(false);
                QueueActivation(request);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                QueueError("Single-instance activation listener", exception);
                try
                {
                    await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static async Task<InstanceActivationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The activation message length is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<InstanceActivationRequest>(payload)
            ?? throw new InvalidDataException("The activation message is empty.");
    }

    private void QueueActivation(InstanceActivationRequest request)
    {
        var handlers = ActivationReceived;
        if (handlers is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var args = new InstanceActivationEventArgs(request);
            foreach (EventHandler<InstanceActivationEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception exception)
                {
                    QueueError("Single-instance activation handler", exception);
                }
            }
        });
    }

    private void QueueError(string operation, Exception exception)
    {
        var handlers = PlatformError;
        if (handlers is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var args = new PlatformErrorEventArgs(operation, exception);
            foreach (EventHandler<PlatformErrorEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch
                {
                    // Platform-error observers must not stop the listener.
                }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        var listener = _listenerTask;
        if (listener is not null && Task.CurrentId != listener.Id)
        {
            try
            {
                listener.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        _shutdown.Dispose();
        _lifetimeMutex.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        var listener = _listenerTask;
        if (listener is not null && Task.CurrentId != listener.Id)
        {
            try
            {
                await listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        _shutdown.Dispose();
        _lifetimeMutex.Dispose();
        GC.SuppressFinalize(this);
    }
}
