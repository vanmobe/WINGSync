using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Wapi;

/// <summary>
/// Runs one process-isolated x32ram/wapi connection and exposes a typed async session.
/// </summary>
public sealed class WapiProcessSession : IWingSession
{
    private const int MaxWireLineLength = 1_048_576;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(12);

    private readonly string role;
    private readonly string helperPath;
    private readonly ISyncObserver observer;
    private readonly IClock clock;
    private readonly TimeSpan commandTimeout;

    // Process lifecycle and wire-command ownership are distinct. A lifecycle
    // change may stop a helper only after the single FIFO command lease is resolved.
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim commandGate = new(1, 1);

    // Request IDs correlate asynchronous stdout frames with the command that
    // owns them; the process generation rejects late frames from a replaced helper.
    private readonly ConcurrentDictionary<string, PendingRequest> pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Process? process;
    private StreamWriter? input;
    private Task? outputTask;
    private Task? errorTask;
    private CancellationTokenSource? processCancellation;
    private TaskCompletionSource<bool>? readySource;
    private long requestSequence;
    private long processGeneration;
    private long activeProcessGeneration;
    private bool disposed;
    private WingSessionState state = WingSessionState.Disconnected;

    /// <summary>Initializes a disconnected process-backed session.</summary>
    public WapiProcessSession(
        string role,
        string helperPath,
        ISyncObserver observer,
        IClock? clock = null,
        TimeSpan? commandTimeout = null)
    {
        this.role = string.IsNullOrWhiteSpace(role) ? "WING" : role.Trim();
        this.helperPath = Path.GetFullPath(helperPath ?? throw new ArgumentNullException(nameof(helperPath)));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.clock = clock ?? new SystemClock();
        this.commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        if (this.commandTimeout < TimeSpan.FromMilliseconds(100) ||
            this.commandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                "The helper timeout must be between 100 ms and 2 minutes.");
        }
    }

    /// <inheritdoc />
    public event EventHandler<WingParameter>? ParameterChanged;

    /// <inheritdoc />
    public event EventHandler<WingSessionStateChange>? StateChanged;

    /// <inheritdoc />
    public WingSessionState State => state;

    /// <inheritdoc />
    public WingEndpoint? Endpoint { get; private set; }

    /// <inheritdoc />
    public async Task ConnectAsync(WingEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ThrowIfDisposed();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ConnectAsync is also the coordinator's recovery primitive. Always
            // replace the helper/TCP session, even when the endpoint is unchanged,
            // because a timed-out helper can still report Connected locally.
            await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
            Endpoint = endpoint;
            ChangeState(WingSessionState.Connecting, $"Verbinden met {endpoint.IpAddress}.");
            await StartProcessAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteForCompletionAsync(
                    $"CONNECT|{{0}}|{ValidateWireAtom(endpoint.IpAddress, nameof(endpoint))}",
                    cancellationToken)
                .ConfigureAwait(false);

            ChangeState(WingSessionState.Connected, $"Verbonden met {endpoint.IpAddress}.");
        }
        catch (Exception exception)
        {
            ChangeState(
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? WingSessionState.Disconnected
                    : WingSessionState.Faulted,
                exception.Message);
            await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WingParameter>> SnapshotAsync(
        string nodeToken,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        var canonicalNode = ValidateToken(nodeToken);
        var pendingRequest = await ExecuteAsync(
                $"SNAPSHOT|{{0}}|{canonicalNode}",
                PendingKind.Snapshot,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await pendingRequest.SnapshotCompletion.Task
                .WaitAsync(commandTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WapiException exception) when (
            exception.ErrorCode.Equals("WAPI_0", StringComparison.OrdinalIgnoreCase) &&
            exception.NativeMessage.Contains(
                "returned no data",
                StringComparison.OrdinalIgnoreCase))
        {
            // Dynamic plugin nodes only exist while their model is instantiated.
            // Native WAPI reports an absent node as WAPI_0/no-data instead of an
            // empty snapshot. Preserve that distinction from transport faults.
            return Array.Empty<WingParameter>();
        }
        catch (WapiException exception)
        {
            throw new WapiException(
                exception.ErrorCode,
                $"snapshot {canonicalNode}: {exception.NativeMessage}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A native SNAPSHOT can still be running after managed cancellation.
            // Kill this helper generation immediately: a graceful QUIT would be
            // queued behind the snapshot and could let later native work execute.
            ChangeState(
                WingSessionState.Faulted,
                "A WAPI snapshot was canceled after dispatch; the helper will be safely restarted.");
            await StopProcessAsync(CancellationToken.None, forceKill: true).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException exception)
        {
            ChangeState(WingSessionState.Faulted, exception.Message);
            await StopProcessAsync(CancellationToken.None, forceKill: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            pending.TryRemove(pendingRequest.Id, out _);
            commandGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string tokenPath, WingValue value, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var canonicalToken = ValidateToken(tokenPath);
        var encodedValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(FormatValue(value)));
        await ExecuteForCompletionAsync(
                $"SET|{{0}}|{canonicalToken}|{TypeCode(value.Type)}|{encodedValue}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetManyAsync(
        IReadOnlyList<WingWriteRequest> writes,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return;
        }

        if (writes.Count > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(writes), "Een WAPI-batch bevat maximaal 512 waarden.");
        }

        var command = new StringBuilder("SETMANY|{0}");
        foreach (var write in writes)
        {
            ArgumentNullException.ThrowIfNull(write);
            command.Append('|').Append(ValidateToken(write.TokenPath));
            command.Append('|').Append(TypeCode(write.Value.Type));
            command.Append('|').Append(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(FormatValue(write.Value))));
        }

        await ExecuteForCompletionAsync(command.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await ExecuteForCompletionAsync("PING|{0}", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is not null && !process.HasExited && State != WingSessionState.Disconnected)
            {
                try
                {
                    await ExecuteForCompletionAsync("DISCONNECT|{0}", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
                {
                    Record(DiagnosticSeverity.Warning, "WAPI_DISCONNECT_FAILED", exception.Message);
                }
            }

            await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
            ChangeState(WingSessionState.Disconnected, "Connection closed.");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetimeCancellation.Cancel();

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        var commandGateHeld = false;
        try
        {
            await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
            // StopProcess fails every active request. Wait until its owner has
            // released the command lease before disposing the semaphore.
            await commandGate.WaitAsync().ConfigureAwait(false);
            commandGateHeld = true;
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
            if (commandGateHeld)
            {
                // The session is disposed, so no future caller may acquire this
                // terminal lease. Dispose it while it is held.
                commandGate.Dispose();
            }

            lifetimeCancellation.Dispose();
        }
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                "The isolated WAPI helper is missing. Reinstall WingSync.",
                helperPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!,
        };
        startInfo.Environment["WINGSYNC_ROLE"] = role;

        // Publish the generation before starting readers so every callback can
        // prove that it belongs to this exact helper process.
        var generation = Interlocked.Increment(ref processGeneration);
        Volatile.Write(ref activeProcessGeneration, generation);
        processCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token);
        var localReadySource = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        readySource = localReadySource;
        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;

        if (!process.Start())
        {
            throw new InvalidOperationException("The WAPI helper could not be started.");
        }

        input = process.StandardInput;
        input.AutoFlush = true;
        outputTask = ReadOutputAsync(
            process.StandardOutput,
            generation,
            localReadySource,
            processCancellation.Token);
        errorTask = ReadErrorAsync(
            process.StandardError,
            generation,
            processCancellation.Token);

        await localReadySource.Task.WaitAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingRequest> ExecuteAsync(
        string commandTemplate,
        PendingKind kind,
        CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref activeProcessGeneration);
            if (generation == 0 ||
                process is null ||
                process.HasExited ||
                !IsCurrentProcessGeneration(generation))
            {
                throw CreateHelperClosedException();
            }

            var writer = input ?? throw CreateHelperClosedException();
            var id = Interlocked.Increment(ref requestSequence).ToString(CultureInfo.InvariantCulture);
            var command = string.Format(CultureInfo.InvariantCulture, commandTemplate, id);
            if (command.Length > MaxWireLineLength)
            {
                throw new InvalidOperationException("The IPC command is too large.");
            }

            var request = new PendingRequest(
                id,
                kind,
                generation);
            if (!pending.TryAdd(id, request))
            {
                throw new InvalidOperationException("Could not reserve a unique IPC request ID.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Once a complete command can cross helper stdin, caller cancellation
                // must not orphan its terminal ACK. Shutdown/reconnect may only proceed
                // after the bounded request completion or a transport fault.
                await writer.WriteLineAsync(command.AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                // The caller owns the command lease until the terminal ACK/END,
                // cancellation recovery, or timeout. WAPI is FIFO; retaining this
                // lease prevents a write from being queued behind an in-flight
                // snapshot that may have to be killed.
                return request;
            }
            catch
            {
                pending.TryRemove(id, out _);
                throw;
            }
        }
        catch
        {
            commandGate.Release();
            throw;
        }
    }

    private async Task ExecuteForCompletionAsync(
        string commandTemplate,
        CancellationToken cancellationToken)
    {
        var request = await ExecuteAsync(commandTemplate, PendingKind.Completion, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await request.Completion.Task
                .WaitAsync(commandTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            ChangeState(WingSessionState.Faulted, exception.Message);
            // The native outcome is indeterminate without a terminal ACK. Kill the
            // FIFO generation before releasing its command lease so no queued work
            // can execute behind it.
            await StopProcessAsync(CancellationToken.None, forceKill: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            pending.TryRemove(request.Id, out _);
            commandGate.Release();
        }
    }

    private async Task ReadOutputAsync(
        StreamReader reader,
        long generation,
        TaskCompletionSource<bool> localReadySource,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!IsCurrentProcessGeneration(generation))
                {
                    break;
                }

                if (line.Length > MaxWireLineLength)
                {
                    throw new InvalidDataException("The WAPI helper sent an IPC line that is too large.");
                }

                HandleOutputLine(line, generation, localReadySource);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            if (!IsCurrentProcessGeneration(generation))
            {
                return;
            }

            FailAllPending(exception, generation, localReadySource);
            if (IsCurrentProcessGeneration(generation))
            {
                ChangeState(WingSessionState.Faulted, exception.Message);
            }
        }
    }

    private async Task ReadErrorAsync(
        StreamReader reader,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!IsCurrentProcessGeneration(generation))
                {
                    break;
                }

                Record(
                    DiagnosticSeverity.Trace,
                    "WAPI_HELPER_STDERR",
                    line.Length <= 2_048 ? line : line[..2_048]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void HandleOutputLine(
        string line,
        long generation,
        TaskCompletionSource<bool> localReadySource)
    {
        if (!IsCurrentProcessGeneration(generation))
        {
            return;
        }

        if (line == "READY")
        {
            localReadySource.TrySetResult(true);
            return;
        }

        // Stdout is reserved for this framed protocol. Values and free-form
        // details are Base64 encoded so the pipe separator remains unambiguous.
        var parts = line.Split('|');
        if (parts.Length == 0)
        {
            return;
        }

        switch (parts[0])
        {
            case "OK" when parts.Length >= 2:
                CompleteRequest(parts[1], generation);
                break;

            case "ERROR" when parts.Length >= 4:
                FailRequest(
                    parts[1],
                    generation,
                    new WapiException(parts[2], Decode(parts[3])));
                break;

            case "ITEM" when parts.Length >= 5:
                AddSnapshotItem(parts[1], parts[2], parts[3], parts[4], generation);
                break;

            case "END" when parts.Length >= 3:
                CompleteSnapshot(parts[1], parts[2], generation);
                break;

            case "EVENT" when parts.Length >= 4:
                RaiseParameter(parts[1], parts[2], parts[3], generation);
                break;

            case "STATE" when parts.Length >= 4:
                HandleState(parts[2], Decode(parts[3]), generation);
                break;

            default:
                Record(DiagnosticSeverity.Warning, "WAPI_PROTOCOL_UNKNOWN", $"Unknown helper message: {parts[0]}");
                break;
        }
    }

    private void CompleteRequest(string id, long generation)
    {
        if (pending.TryGetValue(id, out var request) &&
            request.Generation == generation &&
            pending.TryRemove(id, out request))
        {
            request.Completion.TrySetResult(true);
        }
    }

    private void FailRequest(string id, long generation, Exception exception)
    {
        if (!pending.TryGetValue(id, out var request) ||
            request.Generation != generation ||
            !pending.TryRemove(id, out request))
        {
            return;
        }

        request.Completion.TrySetException(exception);
        request.SnapshotCompletion.TrySetException(exception);
    }

    private void AddSnapshotItem(
        string id,
        string token,
        string type,
        string encodedValue,
        long generation)
    {
        if (!pending.TryGetValue(id, out var request) ||
            request.Generation != generation ||
            request.Kind != PendingKind.Snapshot)
        {
            return;
        }

        var decoded = Decode(encodedValue);
        request.Items.Add(
                new WingParameter(
                CanonicalPathFromWire(token),
                ParseValue(type, decoded),
                clock.UtcNow));
    }

    private void CompleteSnapshot(string id, string countText, long generation)
    {
        if (!pending.TryGetValue(id, out var request) ||
            request.Generation != generation ||
            request.Kind != PendingKind.Snapshot ||
            !pending.TryRemove(id, out request))
        {
            return;
        }

        if (!int.TryParse(
                countText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var count) ||
            count < 0)
        {
            request.SnapshotCompletion.TrySetException(
                new InvalidDataException(
                    $"Snapshot contains an invalid helper count '{countText}'."));
            return;
        }

        if (count != request.Items.Count)
        {
            // END is the snapshot commit marker. A mismatched count means the
            // stream was truncated and none of its partial values may be trusted.
            request.SnapshotCompletion.TrySetException(
                new InvalidDataException(
                    $"Snapshot was incomplete: helper reported {count}, received {request.Items.Count}."));
            return;
        }

        request.SnapshotCompletion.TrySetResult(request.Items.ToArray());
    }

    private void RaiseParameter(
        string token,
        string type,
        string encodedValue,
        long generation)
    {
        try
        {
            if (!IsCurrentProcessGeneration(generation))
            {
                return;
            }

            var parameter = new WingParameter(
                CanonicalPathFromWire(token),
                ParseValue(type, Decode(encodedValue)),
                clock.UtcNow);
            if (IsCurrentProcessGeneration(generation))
            {
                ParameterChanged?.Invoke(this, parameter);
            }
        }
        catch (Exception exception)
        {
            Record(DiagnosticSeverity.Error, "EVENT_HANDLER_FAILED", exception.Message);
        }
    }

    private void HandleState(string status, string detail, long generation)
    {
        if (!IsCurrentProcessGeneration(generation))
        {
            return;
        }

        var newState = status.ToUpperInvariant() switch
        {
            "CONNECTED" => WingSessionState.Connected,
            "CONNECTING" => WingSessionState.Connecting,
            "RECONNECTING" => WingSessionState.Reconnecting,
            "FAULTED" or "ERROR" => WingSessionState.Faulted,
            _ => WingSessionState.Disconnected,
        };
        if (IsCurrentProcessGeneration(generation))
        {
            ChangeState(newState, detail);
        }
    }

    private async Task StopProcessAsync(
        CancellationToken cancellationToken,
        bool forceKill = false)
    {
        // Invalidate the current helper before waiting for its readers. A subscriber
        // can legitimately keep an old reader blocked beyond the bounded shutdown
        // wait; generation checks ensure it can never complete requests or publish
        // state/events into a replacement helper session afterwards.
        var stoppedGeneration = Interlocked.Exchange(ref activeProcessGeneration, 0);
        var localProcessCancellation = processCancellation;
        processCancellation = null;
        localProcessCancellation?.Cancel();
        var localReadySource = readySource;
        readySource = null;
        var localProcess = process;
        process = null;
        var localInput = input;
        input = null;
        var localOutputTask = outputTask;
        outputTask = null;
        var localErrorTask = errorTask;
        errorTask = null;

        if (localProcess is null)
        {
            try
            {
                await AwaitReaderTasksAsync(localOutputTask, localErrorTask).ConfigureAwait(false);
            }
            finally
            {
                localProcessCancellation?.Dispose();
            }

            return;
        }

        try
        {
            if (forceKill && !localProcess.HasExited)
            {
                // Kill synchronously before completing managed waiters. This is
                // intentionally not a graceful stop: QUIT is FIFO and could sit
                // behind the timed-out native snapshot.
                localProcess.Kill(entireProcessTree: true);
            }
            else if (!localProcess.HasExited && localInput is not null)
            {
                try
                {
                    await localInput.WriteLineAsync("QUIT|shutdown".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    await localInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await localProcess.WaitForExitAsync(cancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
                {
                    Record(DiagnosticSeverity.Trace, "WAPI_HELPER_GRACEFUL_STOP_FAILED", exception.Message);
                }
            }

            if (!localProcess.HasExited)
            {
                localProcess.Kill(entireProcessTree: true);
                await localProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            localInput?.Dispose();
            localProcess.Dispose();
            FailAllPending(
                new IOException("The WAPI helper stopped."),
                stoppedGeneration,
                localReadySource);
        }

        try
        {
            await AwaitReaderTasksAsync(localOutputTask, localErrorTask).ConfigureAwait(false);
        }
        finally
        {
            localProcessCancellation?.Dispose();
        }
    }

    private async Task AwaitReaderTasksAsync(Task? localOutputTask, Task? localErrorTask)
    {
        var readers = new[] { localOutputTask, localErrorTask }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (readers.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(readers)
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or TimeoutException)
        {
            Record(DiagnosticSeverity.Trace, "WAPI_HELPER_READER_STOP_FAILED", exception.Message);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var generation = Volatile.Read(ref activeProcessGeneration);
        if (disposed ||
            generation == 0 ||
            State == WingSessionState.Disconnected ||
            !ReferenceEquals(sender, process))
        {
            return;
        }

        var exitCode = sender is Process exitedProcess ? exitedProcess.ExitCode : -1;
        var exception = new IOException($"The WAPI helper stopped unexpectedly (exit code {exitCode}).");
        FailAllPending(
            exception,
            generation,
            readySource);
        ChangeState(WingSessionState.Faulted, exception.Message);
    }

    private IOException CreateHelperClosedException()
    {
        try
        {
            if (process is { HasExited: true } exitedProcess)
            {
                return new IOException(
                    $"The WAPI helper stopped unexpectedly (exit code {exitedProcess.ExitCode}).");
            }
        }
        catch (InvalidOperationException)
        {
            // The process can be disposed concurrently by the lifecycle recovery path.
        }

        return new IOException("The WAPI helper closed unexpectedly.");
    }

    private void FailAllPending(
        Exception exception,
        long generation,
        TaskCompletionSource<bool>? localReadySource)
    {
        foreach (var pair in pending.ToArray())
        {
            if (pair.Value.Generation != generation)
            {
                continue;
            }

            if (!pending.TryRemove(pair.Key, out var request))
            {
                continue;
            }

            request.Completion.TrySetException(exception);
            request.SnapshotCompletion.TrySetException(exception);
        }

        localReadySource?.TrySetException(exception);
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (State != WingSessionState.Connected)
        {
            throw new InvalidOperationException($"{role} is not connected.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private bool IsCurrentProcessGeneration(long generation) =>
        !disposed &&
        generation != 0 &&
        Volatile.Read(ref activeProcessGeneration) == generation;

    private void ChangeState(WingSessionState newState, string reason)
    {
        state = newState;
        StateChanged?.Invoke(this, new WingSessionStateChange(newState, reason, clock.UtcNow));
    }

    private void Record(DiagnosticSeverity severity, string code, string message) =>
        observer.Record(new DiagnosticEvent(severity, code, message, role, clock.UtcNow));

    private static string ValidateToken(string token)
    {
        var normalized = ToWireToken(token);
        if (normalized.Length is < 1 or > 256 ||
            normalized.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '$' or '-')))
        {
            throw new ArgumentException("Invalid WAPI token name.", nameof(token));
        }

        return normalized;
    }

    private static string ValidateWireAtom(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.IndexOfAny(['|', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Invalid IPC value.", parameterName);
        }

        return value;
    }

    private static string ToWireToken(string token) =>
        token.Trim().TrimStart('/').Replace('/', '.').ToLowerInvariant();

    private static string CanonicalPathFromWire(string token)
    {
        var trimmed = token.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            throw new InvalidDataException("The WAPI helper sent an empty token name.");
        }

        return "/" + trimmed.Replace('.', '/').ToLowerInvariant();
    }

    private static string Decode(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Invalid base64 data from the WAPI helper.", exception);
        }
    }

    private static string FormatValue(WingValue value) =>
        value.Type switch
        {
            WingValueType.I => value.AsInt32().ToString(CultureInfo.InvariantCulture),
            WingValueType.F => value.AsFloat().ToString("R", CultureInfo.InvariantCulture),
            WingValueType.S => value.AsString(),
            _ => throw new InvalidOperationException("Onbekend WING-waardetype."),
        };

    private static string TypeCode(WingValueType valueType) =>
        valueType switch
        {
            WingValueType.I => "I",
            WingValueType.F => "F",
            WingValueType.S => "S",
            _ => throw new InvalidOperationException("Onbekend WING-waardetype."),
        };

    private static WingValue ParseValue(string type, string value) =>
        type.ToUpperInvariant() switch
        {
            "I" => WingValue.FromInt32(int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            "F" => WingValue.FromFloat(float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "S" => WingValue.FromString(value),
            _ => throw new InvalidDataException($"Onbekend WAPI-waardetype '{type}'."),
        };

    private enum PendingKind
    {
        Completion,
        Snapshot,
    }

    private sealed class PendingRequest
    {
        public PendingRequest(string id, PendingKind kind, long generation)
        {
            Id = id;
            Kind = kind;
            Generation = generation;
        }

        public string Id { get; }

        public PendingKind Kind { get; }

        public long Generation { get; }

        public List<WingParameter> Items { get; } = [];

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<WingParameter>> SnapshotCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Exception containing the numeric status reported by wapi.</summary>
public sealed class WapiException : IOException
{
    /// <summary>Initializes an exception returned by the native helper.</summary>
    public WapiException(string errorCode, string message)
        : base($"WAPI error {errorCode}: {message}")
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "UNKNOWN" : errorCode;
        NativeMessage = message ?? string.Empty;
    }

    /// <summary>Gets the native wapi status code.</summary>
    public string ErrorCode { get; }

    /// <summary>Gets the unformatted diagnostic returned by native wapi.</summary>
    public string NativeMessage { get; }
}
