namespace WingSync.Infrastructure.Diagnostics;

/// <summary>
/// Defines the severity of a structured WingSync diagnostic entry.
/// </summary>
public enum WingLogLevel
{
    /// <summary>
    /// Very detailed diagnostic information.
    /// </summary>
    Trace,

    /// <summary>
    /// Developer-oriented diagnostic information.
    /// </summary>
    Debug,

    /// <summary>
    /// Normal operational information.
    /// </summary>
    Information,

    /// <summary>
    /// A recoverable problem or degraded condition.
    /// </summary>
    Warning,

    /// <summary>
    /// An operation failed.
    /// </summary>
    Error,

    /// <summary>
    /// The application cannot continue a critical function safely.
    /// </summary>
    Critical,
}
