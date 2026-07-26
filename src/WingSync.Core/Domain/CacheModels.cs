namespace WingSync.Core.Domain;

/// <summary>
/// Describes whether cached state may still be used for display and diff calculation.
/// </summary>
public enum CacheFreshness
{
    /// <summary>No successful live observation has been made.</summary>
    Unknown,

    /// <summary>The entry belongs to the current healthy connection epoch.</summary>
    Fresh,

    /// <summary>The connection was lost or an event gap made the entry unreliable.</summary>
    Stale,
}

/// <summary>
/// Carries identity, version, ordering, and freshness information for a cached value.
/// </summary>
/// <param name="ConsoleSerial">The stable console serial used as cache partition key.</param>
/// <param name="ConsoleModel">The console model observed for this connection.</param>
/// <param name="FirmwareVersion">The console firmware version.</param>
/// <param name="WapiVersion">The WAPI header/library version encoded as <c>0xMMmmVVuu</c>.</param>
/// <param name="ObservedAt">When the value was last observed from the live console.</param>
/// <param name="ConnectionEpoch">A locally monotonic identifier incremented after every reconnect.</param>
/// <param name="Revision">A locally monotonic revision within the epoch.</param>
/// <param name="Freshness">The current freshness classification.</param>
/// <param name="ParameterModel">The active dynamic EQ, gate, or dynamics model, when applicable.</param>
public sealed record CacheMetadata(
    string ConsoleSerial,
    string ConsoleModel,
    string FirmwareVersion,
    uint WapiVersion,
    DateTimeOffset ObservedAt,
    long ConnectionEpoch,
    long Revision,
    CacheFreshness Freshness,
    string? ParameterModel = null);

/// <summary>
/// Stores one canonical token value with the metadata needed to judge its authority.
/// </summary>
/// <param name="TokenPath">The canonical descriptor returned by WAPI, including its leading slash.</param>
/// <param name="Value">The strongly typed value.</param>
/// <param name="Metadata">The observation and console metadata.</param>
public sealed record CachedWingValue(
    string TokenPath,
    WingValue Value,
    CacheMetadata Metadata);
