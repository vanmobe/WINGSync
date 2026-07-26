using System.Collections.Concurrent;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Simulation;

/// <summary>
/// Deterministic in-memory WING used by demo mode, fault injection, and integration tests.
/// </summary>
public sealed class SimulatedWingSession : IWingSession
{
    private readonly ConcurrentDictionary<string, WingValue> state;
    private readonly IClock clock;
    private readonly TimeSpan operationDelay;
    private readonly TimeSpan writeDelay;
    private readonly object faultSync = new();
    private Exception? nextFailure;
    private bool disposed;

    /// <summary>Initializes a simulator with an optional state map and operation latency.</summary>
    public SimulatedWingSession(
        IReadOnlyDictionary<string, WingValue>? initialState = null,
        IClock? clock = null,
        TimeSpan? operationDelay = null,
        TimeSpan? writeDelay = null)
    {
        state = new ConcurrentDictionary<string, WingValue>(
            (initialState ?? new Dictionary<string, WingValue>())
                .Select(static pair =>
                    new KeyValuePair<string, WingValue>(NormalizePath(pair.Key), pair.Value)),
            StringComparer.Ordinal);
        this.clock = clock ?? new SystemClock();
        this.operationDelay = operationDelay ?? TimeSpan.Zero;
        this.writeDelay = writeDelay ?? this.operationDelay;
    }

    /// <inheritdoc />
    public event EventHandler<WingParameter>? ParameterChanged;

    /// <inheritdoc />
    public event EventHandler<WingSessionStateChange>? StateChanged;

    /// <inheritdoc />
    public WingSessionState State { get; private set; } = WingSessionState.Disconnected;

    /// <inheritdoc />
    public WingEndpoint? Endpoint { get; private set; }

    /// <summary>Gets an immutable copy of the current simulator state.</summary>
    public IReadOnlyDictionary<string, WingValue> CurrentState =>
        new Dictionary<string, WingValue>(state, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task ConnectAsync(WingEndpoint endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ChangeState(WingSessionState.Connecting, "Simulatie maakt verbinding.");
        await DelayAndMaybeFail(cancellationToken).ConfigureAwait(false);
        ChangeState(WingSessionState.Connected, "Simulator verbonden.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WingParameter>> SnapshotAsync(
        string nodeToken,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        await DelayAndMaybeFail(cancellationToken).ConfigureAwait(false);
        var node = NormalizePath(nodeToken);
        return state
            .Where(pair => pair.Key == node || pair.Key.StartsWith(node + "/", StringComparison.Ordinal))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WingParameter(pair.Key, pair.Value, clock.UtcNow))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task SetAsync(string tokenPath, WingValue value, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await DelayAndMaybeFail(cancellationToken, writeDelay).ConfigureAwait(false);
        var token = NormalizePath(tokenPath);
        var quantized = Quantize(value);
        state[token] = quantized;
        ParameterChanged?.Invoke(this, new WingParameter(token, quantized, clock.UtcNow));
    }

    /// <inheritdoc />
    public async Task SetManyAsync(
        IReadOnlyList<WingWriteRequest> writes,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(writes);
        await DelayAndMaybeFail(cancellationToken, writeDelay).ConfigureAwait(false);

        var validated = writes
            .Select(static write => new WingWriteRequest(
                NormalizePath(write.TokenPath),
                Quantize(write.Value)))
            .ToArray();
        foreach (var write in validated)
        {
            state[write.TokenPath] = write.Value;
        }

        foreach (var write in validated)
        {
            ParameterChanged?.Invoke(
                this,
                new WingParameter(write.TokenPath, write.Value, clock.UtcNow));
        }
    }

    /// <inheritdoc />
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await DelayAndMaybeFail(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!disposed)
        {
            ChangeState(WingSessionState.Disconnected, "Simulator losgekoppeld.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Emits a console-originated change.</summary>
    public void ChangeFromConsole(string tokenPath, WingValue value)
    {
        EnsureConnected();
        var token = NormalizePath(tokenPath);
        var quantized = Quantize(value);
        state[token] = quantized;
        ParameterChanged?.Invoke(this, new WingParameter(token, quantized, clock.UtcNow));
    }

    /// <summary>Causes the next operation to fail with the supplied exception.</summary>
    public void FailNextOperation(Exception exception)
    {
        lock (faultSync)
        {
            nextFailure = exception ?? throw new ArgumentNullException(nameof(exception));
        }
    }

    /// <summary>Simulates an immediate transport loss.</summary>
    public void DropConnection(string reason = "Gesimuleerd netwerkverlies.")
    {
        EnsureConnected();
        ChangeState(WingSessionState.Faulted, reason);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        disposed = true;
        State = WingSessionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private async Task DelayAndMaybeFail(
        CancellationToken cancellationToken,
        TimeSpan? delayOverride = null)
    {
        var delay = delayOverride ?? operationDelay;
        if (delay > TimeSpan.Zero)
        {
            await clock.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        Exception? failure;
        lock (faultSync)
        {
            failure = nextFailure;
            nextFailure = null;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private void ChangeState(WingSessionState newState, string reason)
    {
        State = newState;
        StateChanged?.Invoke(this, new WingSessionStateChange(newState, reason, clock.UtcNow));
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (State != WingSessionState.Connected)
        {
            throw new InvalidOperationException("De gesimuleerde WING is niet verbonden.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private static string NormalizePath(string token)
    {
        var trimmed = token.Trim().Trim('/');
        return "/" + trimmed.Replace('.', '/').ToLowerInvariant();
    }

    private static WingValue Quantize(WingValue value) =>
        value.Type == WingValueType.F
            ? WingValue.FromFloat(MathF.Round(value.AsFloat(), 4, MidpointRounding.AwayFromZero))
            : value;
}

/// <summary>Creates deterministic sessions in the order supplied by the caller.</summary>
public sealed class SimulatedWingSessionFactory : IWingSessionFactory
{
    private readonly Queue<IWingSession>? sessions;
    private readonly Func<string, IWingSession>? sessionFactory;

    /// <summary>Initializes a factory with pre-created simulator sessions.</summary>
    public SimulatedWingSessionFactory(params IWingSession[] sessions)
    {
        this.sessions = new Queue<IWingSession>(sessions ?? throw new ArgumentNullException(nameof(sessions)));
    }

    /// <summary>
    /// Initializes a reusable factory. The delegate must return a fresh session for every call.
    /// </summary>
    public SimulatedWingSessionFactory(Func<string, IWingSession> sessionFactory)
    {
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <inheritdoc />
    public IWingSession Create(string role)
    {
        if (sessionFactory is not null)
        {
            return sessionFactory(role);
        }

        return sessions!.Count > 0
            ? sessions.Dequeue()
            : throw new InvalidOperationException($"Geen gesimuleerde sessie beschikbaar voor {role}.");
    }
}
