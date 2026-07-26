using WingSync.Core.Domain;

namespace WingSync.Core.Abstractions;

/// <summary>Severity used by diagnostics, the status banner, and structured logs.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Verbose operational detail.</summary>
    Trace,

    /// <summary>Normal lifecycle or synchronization information.</summary>
    Information,

    /// <summary>Recoverable condition that deserves operator attention.</summary>
    Warning,

    /// <summary>Failure that pauses or blocks part of synchronization.</summary>
    Error,

    /// <summary>Safety or identity failure that blocks all writes.</summary>
    Critical,
}
/// <summary>
/// One structured diagnostic event. Fields must not contain credentials or arbitrary
/// environment-variable values.
/// </summary>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Concise operator-readable message.</param>
/// <param name="Source">Component or console role.</param>
/// <param name="OccurredAt">UTC timestamp.</param>
/// <param name="Properties">Optional bounded structured context.</param>
public sealed record DiagnosticEvent(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string Source,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>Receives structured lifecycle, preview, write, and verification events.</summary>
public interface ISyncObserver
{
    /// <summary>Records a structured diagnostic event.</summary>
    void Record(DiagnosticEvent diagnosticEvent);

    /// <summary>Records a safe preview or completed write without exposing WAPI APIs.</summary>
    void RecordParameterAction(
        string action,
        string sourceToken,
        string targetToken,
        SyncScope scope,
        WingValue value,
        bool dryRun);
}
