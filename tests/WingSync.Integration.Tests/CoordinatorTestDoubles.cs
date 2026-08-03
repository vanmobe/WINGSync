using System.Collections.Concurrent;
using System.Threading.Channels;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.Integration.Tests;

internal sealed class ScriptedWingSession : IWingSession
{
    private readonly ConcurrentDictionary<string, WingValue> state;
    private readonly ConcurrentQueue<IReadOnlyList<WingWriteRequest>> requestedBatches = new();
    private readonly ConcurrentQueue<string> snapshotRequests = new();
    private readonly ConcurrentDictionary<string, int> snapshotInvocationCounts =
        new(StringComparer.Ordinal);
    private bool disposed;
    private int connectCount;
    private int disconnectCount;
    private int disposeCount;
    private int setManyInvocationCount;

    public ScriptedWingSession(IReadOnlyDictionary<string, WingValue>? initialState = null)
    {
        state = new ConcurrentDictionary<string, WingValue>(
            (initialState ?? new Dictionary<string, WingValue>())
                .Select(static pair =>
                    new KeyValuePair<string, WingValue>(Normalize(pair.Key), pair.Value)),
            StringComparer.Ordinal);
    }

    public event EventHandler<WingParameter>? ParameterChanged;

    public event EventHandler<WingSessionStateChange>? StateChanged;

    public WingSessionState State { get; private set; } = WingSessionState.Disconnected;

    public WingEndpoint? Endpoint { get; private set; }

    public bool EmitWriteEvents { get; set; } = true;

    public Func<string, WingValue, WingValue>? WriteTransform { get; set; }

    public Func<
        string,
        IReadOnlyList<WingParameter>,
        IReadOnlyList<WingParameter>>? SnapshotTransform { get; set; }

    public Func<SnapshotCapture, CancellationToken, Task>? SnapshotCapturedAsync { get; set; }

    public Func<SetManyCapture, CancellationToken, Task>? BeforeSetManyAsync { get; set; }

    public Func<CancellationToken, Task>? BeforeDisconnectAsync { get; set; }

    public Func<Task>? BeforeDisposeAsync { get; set; }

    public ReconciliationOperationProbe? OperationProbe { get; set; }

    public IReadOnlyList<IReadOnlyList<WingWriteRequest>> RequestedBatches =>
        requestedBatches.ToArray();

    public IReadOnlyList<WingWriteRequest> RequestedWrites =>
        requestedBatches.SelectMany(static batch => batch).ToArray();

    public IReadOnlyList<string> SnapshotRequests => snapshotRequests.ToArray();

    public int ConnectCount => Volatile.Read(ref connectCount);

    public int DisconnectCount => Volatile.Read(ref disconnectCount);

    public int DisposeCount => Volatile.Read(ref disposeCount);

    public Task ConnectAsync(WingEndpoint endpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Endpoint = endpoint;
        Interlocked.Increment(ref connectCount);
        ChangeState(WingSessionState.Connecting, "test connect");
        ChangeState(WingSessionState.Connected, "test connected");
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<WingParameter>> SnapshotAsync(
        string nodeToken,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        OperationProbe?.EnterSnapshot();
        try
        {
        var node = Normalize(nodeToken);
        snapshotRequests.Enqueue(node);
        var invocation = snapshotInvocationCounts.AddOrUpdate(
            node,
            addValue: 1,
            static (_, current) => checked(current + 1));
        IReadOnlyList<WingParameter> result = state
            .Where(pair =>
                pair.Key.Equals(node, StringComparison.Ordinal) ||
                pair.Key.StartsWith(node + "/", StringComparison.Ordinal))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair =>
                new WingParameter(pair.Key, pair.Value, DateTimeOffset.UtcNow))
            .ToArray();
        result = SnapshotTransform?.Invoke(node, result) ?? result;
        var hook = SnapshotCapturedAsync;
        if (hook is not null)
        {
            await hook(
                    new SnapshotCapture(node, invocation, result),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
        }
        finally
        {
            OperationProbe?.ExitSnapshot();
        }
    }

    public Task SetAsync(
        string tokenPath,
        WingValue value,
        CancellationToken cancellationToken) =>
        SetManyAsync([new WingWriteRequest(tokenPath, value)], cancellationToken);

    public async Task SetManyAsync(
        IReadOnlyList<WingWriteRequest> writes,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(writes);
        cancellationToken.ThrowIfCancellationRequested();
        OperationProbe?.EnterSetMany();
        try
        {

        var requested = writes
            .Select(static write => new WingWriteRequest(Normalize(write.TokenPath), write.Value))
            .ToArray();
        var invocation = Interlocked.Increment(ref setManyInvocationCount);
        var hook = BeforeSetManyAsync;
        if (hook is not null)
        {
            await hook(
                    new SetManyCapture(invocation, requested),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        requestedBatches.Enqueue(requested);

        var applied = requested
            .Select(write => new WingWriteRequest(
                write.TokenPath,
                WriteTransform?.Invoke(write.TokenPath, write.Value) ?? write.Value))
            .ToArray();
        foreach (var write in applied)
        {
            state[write.TokenPath] = write.Value;
        }

        if (EmitWriteEvents)
        {
            foreach (var write in applied)
            {
                RaiseParameter(write.TokenPath, write.Value);
            }
        }

        }
        finally
        {
            OperationProbe?.ExitSetMany();
        }
    }

    public Task PingAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref disconnectCount);
        var hook = BeforeDisconnectAsync;
        if (hook is not null)
        {
            await hook(cancellationToken).ConfigureAwait(false);
        }

        if (!disposed)
        {
            ChangeState(WingSessionState.Disconnected, "test disconnect");
        }
    }

    public void ChangeFromConsole(string tokenPath, WingValue value)
    {
        EnsureConnected();
        var token = Normalize(tokenPath);
        state[token] = value;
        RaiseParameter(token, value);
    }

    public void EmitWithoutPersisting(string tokenPath, WingValue value)
    {
        EnsureConnected();
        RaiseParameter(Normalize(tokenPath), value);
    }

    public void SetSilently(string tokenPath, WingValue value)
    {
        state[Normalize(tokenPath)] = value;
    }

    public void SeedSilently(string tokenPath, WingValue value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        state[Normalize(tokenPath)] = value;
    }

    public bool RemoveSilently(string tokenPath) =>
        state.TryRemove(Normalize(tokenPath), out _);

    public WingValue GetValue(string tokenPath) =>
        state.TryGetValue(Normalize(tokenPath), out var value)
            ? value
            : throw new KeyNotFoundException(tokenPath);

    public bool Contains(string tokenPath) => state.ContainsKey(Normalize(tokenPath));

    public void DropConnection(string reason = "test transport loss")
    {
        EnsureConnected();
        ChangeState(WingSessionState.Faulted, reason);
    }

    public void EmitStateSignal(
        WingSessionState signaledState,
        string reason)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        StateChanged?.Invoke(
            this,
            new WingSessionStateChange(signaledState, reason, DateTimeOffset.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref disposeCount);
        var hook = BeforeDisposeAsync;
        if (hook is not null)
        {
            await hook().ConfigureAwait(false);
        }

        disposed = true;
        State = WingSessionState.Disconnected;
    }

    private void RaiseParameter(string tokenPath, WingValue value) =>
        ParameterChanged?.Invoke(
            this,
            new WingParameter(tokenPath, value, DateTimeOffset.UtcNow));

    private void ChangeState(WingSessionState newState, string reason)
    {
        State = newState;
        StateChanged?.Invoke(
            this,
            new WingSessionStateChange(newState, reason, DateTimeOffset.UtcNow));
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != WingSessionState.Connected)
        {
            throw new InvalidOperationException("Test session is not connected.");
        }
    }

    private static string Normalize(string token)
    {
        var trimmed = token.Trim().Trim('/');
        return "/" + trimmed.Replace('.', '/').ToLowerInvariant();
    }
}

internal sealed record SnapshotCapture(
    string NodeToken,
    int Invocation,
    IReadOnlyList<WingParameter> CapturedValues);

internal sealed record SetManyCapture(
    int Invocation,
    IReadOnlyList<WingWriteRequest> RequestedWrites);

internal sealed class SnapshotGate
{
    private readonly string nodeToken;
    private readonly int invocation;
    private readonly TaskCompletionSource<bool> entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int matched;

    public SnapshotGate(string nodeToken, int invocation)
    {
        this.nodeToken = nodeToken;
        this.invocation = invocation;
    }

    public async Task OnCapturedAsync(
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        if (!capture.NodeToken.Equals(nodeToken, StringComparison.Ordinal) ||
            capture.Invocation != invocation ||
            Interlocked.CompareExchange(ref matched, 1, 0) != 0)
        {
            return;
        }

        entered.TrySetResult(true);
        await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        entered.Task.WaitAsync(timeout);

    public void Release() => released.TrySetResult(true);
}

internal sealed class SetManyGate
{
    private readonly int invocation;
    private readonly TaskCompletionSource<bool> entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int matched;

    public SetManyGate(int invocation = 1)
    {
        this.invocation = invocation;
    }

    public async Task OnBeforeSetManyAsync(
        SetManyCapture capture,
        CancellationToken cancellationToken)
    {
        if (capture.Invocation != invocation ||
            Interlocked.CompareExchange(ref matched, 1, 0) != 0)
        {
            return;
        }

        entered.TrySetResult(true);
        await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        entered.Task.WaitAsync(timeout);

    public void Release() => released.TrySetResult(true);
}

internal sealed class ReconciliationOperationProbe
{
    private int activeSnapshots;
    private int activeSetMany;
    private int maximumConcurrentSetMany;
    private int snapshotSetManyOverlap;

    public int MaximumConcurrentSetMany => Volatile.Read(ref maximumConcurrentSetMany);

    public bool SnapshotSetManyOverlapDetected =>
        Volatile.Read(ref snapshotSetManyOverlap) != 0;

    public void EnterSnapshot()
    {
        Interlocked.Increment(ref activeSnapshots);
        if (Volatile.Read(ref activeSetMany) != 0)
        {
            Interlocked.Exchange(ref snapshotSetManyOverlap, 1);
        }
    }

    public void ExitSnapshot() => Interlocked.Decrement(ref activeSnapshots);

    public void EnterSetMany()
    {
        var current = Interlocked.Increment(ref activeSetMany);
        UpdateMaximum(ref maximumConcurrentSetMany, current);
        if (Volatile.Read(ref activeSnapshots) != 0)
        {
            Interlocked.Exchange(ref snapshotSetManyOverlap, 1);
        }
    }

    public void ExitSetMany() => Interlocked.Decrement(ref activeSetMany);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref maximum, candidate, current) == current)
            {
                return;
            }
        }
    }
}

internal sealed class CoalescingDelayGateClock : IClock
{
    private readonly TaskCompletionSource<bool> entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int gated;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay == TimeSpan.FromMilliseconds(75) &&
            Interlocked.CompareExchange(ref gated, 1, 0) == 0)
        {
            entered.TrySetResult(true);
            await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    public Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        entered.Task.WaitAsync(timeout);

    public void Release() => released.TrySetResult(true);
}

internal sealed class HealthPollGateClock : IClock
{
    private readonly TaskCompletionSource<bool> entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int gated;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay == TimeSpan.FromSeconds(3) &&
            Interlocked.CompareExchange(ref gated, 1, 0) == 0)
        {
            entered.TrySetResult(true);
            await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    public Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        entered.Task.WaitAsync(timeout);

    public void Release() => released.TrySetResult(true);
}

internal sealed class HealthPollPulseClock : IClock
{
    private readonly Channel<TaskCompletionSource<bool>> pulses =
        Channel.CreateUnbounded<TaskCompletionSource<bool>>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay != TimeSpan.FromSeconds(3))
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pulse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await pulses.Writer.WriteAsync(pulse, cancellationToken).ConfigureAwait(false);
        await pulse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AdvanceAsync(TimeSpan timeout)
    {
        var pulse = await pulses.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(timeout)
            .ConfigureAwait(false);
        pulse.TrySetResult(true);
    }
}

internal sealed class ScriptedSessionFactory : IWingSessionFactory
{
    private readonly Queue<IWingSession> sessions;

    public ScriptedSessionFactory(params IWingSession[] sessions)
    {
        this.sessions = new Queue<IWingSession>(sessions);
    }

    public IWingSession Create(string role) =>
        sessions.Count > 0
            ? sessions.Dequeue()
            : throw new InvalidOperationException($"No scripted session for {role}.");
}

internal sealed record RecordedParameterAction(
    string Action,
    string SourceToken,
    string TargetToken,
    SyncScope Scope,
    WingValue Value,
    bool DryRun);

internal sealed class RecordingObserver : ISyncObserver
{
    private readonly ConcurrentQueue<DiagnosticEvent> diagnostics = new();
    private readonly ConcurrentQueue<RecordedParameterAction> actions = new();

    public IReadOnlyList<DiagnosticEvent> Diagnostics => diagnostics.ToArray();

    public IReadOnlyList<RecordedParameterAction> Actions => actions.ToArray();

    public Action<RecordedParameterAction>? ParameterActionRecorded { get; set; }

    public void Record(DiagnosticEvent diagnosticEvent) => diagnostics.Enqueue(diagnosticEvent);

    public void RecordParameterAction(
        string action,
        string sourceToken,
        string targetToken,
        SyncScope scope,
        WingValue value,
        bool dryRun)
    {
        var recorded = new RecordedParameterAction(
            action,
            sourceToken,
            targetToken,
            scope,
            value,
            dryRun);
        actions.Enqueue(recorded);
        ParameterActionRecorded?.Invoke(recorded);
    }
}

internal sealed record CacheEpoch(string Role, string Serial, long Epoch);

internal sealed class RecordingStateSink : IWingStateSink
{
    private readonly ConcurrentQueue<CacheEpoch> epochs = new();
    private readonly ConcurrentQueue<string> staleRoles = new();
    private readonly ConcurrentQueue<WingParameter> stored = new();

    public IReadOnlyList<CacheEpoch> Epochs => epochs.ToArray();

    public IReadOnlyList<string> StaleRoles => staleRoles.ToArray();

    public IReadOnlyList<WingParameter> Stored => stored.ToArray();

    public Task BeginEpochAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        epochs.Enqueue(new CacheEpoch(role, identity.SerialNumber, connectionEpoch));
        return Task.CompletedTask;
    }

    public Task StoreAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        IReadOnlyList<WingParameter> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var parameter in parameters)
        {
            stored.Enqueue(parameter);
        }

        return Task.CompletedTask;
    }

    public Task MarkStaleAsync(
        string role,
        DiscoveredWing identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        staleRoles.Enqueue(role);
        return Task.CompletedTask;
    }
}
