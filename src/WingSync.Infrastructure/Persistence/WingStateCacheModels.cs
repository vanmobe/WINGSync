using System.Collections.ObjectModel;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Forms the stable partition key for one console cache.
/// </summary>
/// <param name="ConsoleSerial">The physical console serial number.</param>
/// <param name="ConsoleModel">The advertised console model.</param>
/// <param name="FirmwareVersion">The firmware version that defines the parameter model.</param>
public sealed record WingCacheIdentity(
    string ConsoleSerial,
    string ConsoleModel,
    string FirmwareVersion);

/// <summary>
/// Represents an observational state snapshot for one exact console/model/firmware identity.
/// </summary>
public sealed class WingStateSnapshot
{
    private readonly ReadOnlyCollection<CachedWingValue> _values;

    /// <summary>
    /// Initializes a new immutable snapshot.
    /// </summary>
    /// <param name="identity">The exact console identity.</param>
    /// <param name="capturedAtUtc">When the snapshot was assembled.</param>
    /// <param name="values">Canonical cached values.</param>
    public WingStateSnapshot(
        WingCacheIdentity identity,
        DateTimeOffset capturedAtUtc,
        IEnumerable<CachedWingValue> values)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentNullException.ThrowIfNull(values);
        CapturedAtUtc = capturedAtUtc;
        _values = Array.AsReadOnly(values.ToArray());
    }

    /// <summary>
    /// Gets the console identity used as the cache key.
    /// </summary>
    public WingCacheIdentity Identity { get; }

    /// <summary>
    /// Gets when this snapshot was assembled.
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>
    /// Gets the immutable canonical values.
    /// </summary>
    public IReadOnlyList<CachedWingValue> Values => _values;
}

/// <summary>
/// Identifies the persistence outcome of loading a console snapshot.
/// </summary>
public enum WingStateCacheLoadStatus
{
    /// <summary>No persisted snapshot exists.</summary>
    Missing,

    /// <summary>The primary snapshot loaded successfully.</summary>
    Loaded,

    /// <summary>The backup loaded after the primary was missing or quarantined.</summary>
    RecoveredFromBackup,

    /// <summary>A corrupt snapshot was quarantined and no usable snapshot remained.</summary>
    CorruptQuarantined,

    /// <summary>The file belongs to an unexpected identity and was quarantined.</summary>
    IdentityMismatchQuarantined,

    /// <summary>The snapshot uses an unsupported schema.</summary>
    UnsupportedSchema,

    /// <summary>An I/O or access failure prevented loading.</summary>
    Failed,
}

/// <summary>
/// Describes a safe cache load. Persisted state is observational and is never an automatic write source.
/// </summary>
/// <param name="Status">The persistence outcome.</param>
/// <param name="Freshness">Fresh, stale, or unknown relative to an explicitly supplied live epoch.</param>
/// <param name="Snapshot">The loaded snapshot, if any.</param>
/// <param name="QuarantinedPath">The path of a quarantined file, if any.</param>
/// <param name="Message">An actionable diagnostic message, if any.</param>
public sealed record WingStateCacheLoadResult(
    WingStateCacheLoadStatus Status,
    CacheFreshness Freshness,
    WingStateSnapshot? Snapshot = null,
    string? QuarantinedPath = null,
    string? Message = null)
{
    /// <summary>
    /// Gets a value that is deliberately always false: disk cache must never be replayed
    /// to a console without fresh live reads and reconciliation.
    /// </summary>
    public bool CanAutomaticallyReplay { get; }
}

/// <summary>
/// Supplies details when a debounced background cache write fails.
/// </summary>
public sealed class WingStateCacheWriteFailedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WingStateCacheWriteFailedEventArgs"/> class.
    /// </summary>
    /// <param name="identity">The affected cache partition.</param>
    /// <param name="exception">The persistence exception.</param>
    public WingStateCacheWriteFailedEventArgs(
        WingCacheIdentity identity,
        Exception exception)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>
    /// Gets the affected console identity.
    /// </summary>
    public WingCacheIdentity Identity { get; }

    /// <summary>
    /// Gets the persistence exception.
    /// </summary>
    public Exception Exception { get; }
}
