using System.Text.Json;

namespace WingSync.Infrastructure.Diagnostics;

/// <summary>
/// Contains redacted exception details stored with a structured log entry.
/// </summary>
/// <param name="Type">The exception type.</param>
/// <param name="Message">The redacted exception message.</param>
/// <param name="StackTrace">The redacted stack trace when available.</param>
public sealed record WingLogExceptionDetails(
    string Type,
    string Message,
    string? StackTrace);

/// <summary>
/// Represents one immutable, redacted JSON-lines diagnostic entry.
/// </summary>
/// <param name="TimestampUtc">The UTC timestamp at which the entry was created.</param>
/// <param name="Level">The entry severity.</param>
/// <param name="EventName">A stable machine-readable event name.</param>
/// <param name="Message">The human-readable redacted message.</param>
/// <param name="Properties">Redacted structured properties.</param>
/// <param name="Exception">Redacted exception details, if any.</param>
public sealed record WingLogEntry(
    DateTimeOffset TimestampUtc,
    WingLogLevel Level,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, JsonElement> Properties,
    WingLogExceptionDetails? Exception);

/// <summary>
/// Supplies an in-memory copy of a persisted structured log entry.
/// </summary>
public sealed class WingLogEntryEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WingLogEntryEventArgs"/> class.
    /// </summary>
    /// <param name="entry">The redacted log entry.</param>
    public WingLogEntryEventArgs(WingLogEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    /// <summary>
    /// Gets the redacted log entry.
    /// </summary>
    public WingLogEntry Entry { get; }
}
