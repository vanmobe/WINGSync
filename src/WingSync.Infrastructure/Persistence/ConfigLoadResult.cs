using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Identifies the outcome of loading the application configuration.
/// </summary>
public enum ConfigLoadStatus
{
    /// <summary>No primary file or usable backup exists.</summary>
    Missing,

    /// <summary>The primary configuration loaded successfully.</summary>
    Loaded,

    /// <summary>The backup loaded after the primary was missing or quarantined.</summary>
    RecoveredFromBackup,

    /// <summary>A corrupt file was quarantined and no usable configuration remained.</summary>
    CorruptQuarantined,

    /// <summary>The file uses a schema version this build cannot safely interpret.</summary>
    UnsupportedSchema,

    /// <summary>An I/O or access failure prevented loading.</summary>
    Failed,
}

/// <summary>
/// Describes a configuration load without hiding recovery, quarantine, or schema problems.
/// </summary>
/// <param name="Status">The load outcome.</param>
/// <param name="Configuration">The loaded configuration, if any.</param>
/// <param name="SchemaVersion">The schema version observed in the selected file.</param>
/// <param name="QuarantinedPath">The path to a quarantined corrupt file, if any.</param>
/// <param name="Message">An actionable diagnostic message, if any.</param>
public sealed record ConfigLoadResult(
    ConfigLoadStatus Status,
    AppConfiguration? Configuration,
    int? SchemaVersion = null,
    string? QuarantinedPath = null,
    string? Message = null);
