using WingSync.Core.Domain;

namespace WingSync.Core.Abstractions;

/// <summary>
/// Describes the lifecycle of one isolated connection to one WING console.
/// </summary>
public enum WingSessionState
{
    /// <summary>No helper process or network connection exists.</summary>
    Disconnected,

    /// <summary>The helper is starting or opening the WAPI connection.</summary>
    Connecting,

    /// <summary>The connection is healthy and may be read.</summary>
    Connected,

    /// <summary>The connection is being re-established; writes must remain paused.</summary>
    Reconnecting,

    /// <summary>The session encountered a terminal error.</summary>
    Faulted,
}

/// <summary>
/// Represents one canonical, typed WING parameter observation.
/// </summary>
/// <param name="TokenPath">Canonical WAPI descriptor such as <c>ch.1.eq.on</c>.</param>
/// <param name="Value">Typed parameter value.</param>
/// <param name="ObservedAt">Local observation timestamp.</param>
public sealed record WingParameter(
    string TokenPath,
    WingValue Value,
    DateTimeOffset ObservedAt);

/// <summary>One typed target value sent as part of an atomic WAPI batch.</summary>
/// <param name="TokenPath">Canonical writable target token.</param>
/// <param name="Value">Typed value.</param>
public sealed record WingWriteRequest(string TokenPath, WingValue Value);

/// <summary>
/// Carries a session state transition and an operator-readable reason.
/// </summary>
/// <param name="State">New session state.</param>
/// <param name="Reason">Concise reason, empty for normal transitions.</param>
/// <param name="OccurredAt">Local transition timestamp.</param>
public sealed record WingSessionStateChange(
    WingSessionState State,
    string Reason,
    DateTimeOffset OccurredAt);

/// <summary>
/// Abstraction implemented by both a WAPI helper process and the deterministic simulator.
/// </summary>
public interface IWingSession : IAsyncDisposable
{
    /// <summary>Raised for unsolicited console parameter changes.</summary>
    event EventHandler<WingParameter>? ParameterChanged;

    /// <summary>Raised whenever connection health changes.</summary>
    event EventHandler<WingSessionStateChange>? StateChanged;

    /// <summary>Gets the most recent state.</summary>
    WingSessionState State { get; }

    /// <summary>Gets the configured endpoint after a successful or attempted connection.</summary>
    WingEndpoint? Endpoint { get; }

    /// <summary>Starts the isolated helper and connects it to the explicit endpoint.</summary>
    Task ConnectAsync(WingEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all typed leaves under a canonical WAPI node. The result is a fresh live
    /// observation and is never satisfied from the local cache.
    /// </summary>
    Task<IReadOnlyList<WingParameter>> SnapshotAsync(
        string nodeToken,
        CancellationToken cancellationToken);

    /// <summary>Sends one typed value to one canonical WAPI token.</summary>
    Task SetAsync(string tokenPath, WingValue value, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a validated group in one native WAPI exchange. Implementations must reject the
    /// complete group before sending when any item is malformed.
    /// </summary>
    Task SetManyAsync(
        IReadOnlyList<WingWriteRequest> writes,
        CancellationToken cancellationToken);

    /// <summary>Performs an explicit WAPI keepalive/health exchange.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Closes the WAPI connection and helper process.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>Creates independent sessions so every console receives its own process boundary.</summary>
public interface IWingSessionFactory
{
    /// <summary>Creates a disconnected session with a human-readable role label.</summary>
    IWingSession Create(string role);
}
