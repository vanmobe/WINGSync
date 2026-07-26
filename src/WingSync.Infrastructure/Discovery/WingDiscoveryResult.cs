using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Discovery;

/// <summary>
/// Identifies the operation that produced a non-fatal discovery issue.
/// </summary>
public enum WingDiscoveryOperation
{
    /// <summary>
    /// Local network adapters could not be enumerated.
    /// </summary>
    EnumerateAdapters,

    /// <summary>
    /// A UDP socket could not be opened or bound.
    /// </summary>
    OpenSocket,

    /// <summary>
    /// A discovery probe could not be sent.
    /// </summary>
    SendProbe,

    /// <summary>
    /// A UDP reply could not be received.
    /// </summary>
    ReceiveReply,
}

/// <summary>
/// Describes a non-fatal problem encountered on one network adapter.
/// </summary>
/// <param name="AdapterName">The adapter name, or an empty string when no adapter was selected.</param>
/// <param name="LocalAddress">The local IPv4 address, or an empty string when it is unknown.</param>
/// <param name="Operation">The operation that failed.</param>
/// <param name="Message">A diagnostic message suitable for logs and the status UI.</param>
public sealed record WingDiscoveryIssue(
    string AdapterName,
    string LocalAddress,
    WingDiscoveryOperation Operation,
    string Message);

/// <summary>
/// Contains all unique WING announcements and non-fatal adapter issues from one discovery pass.
/// </summary>
/// <param name="Wings">The unique announcements collected during the complete reply window.</param>
/// <param name="Issues">Adapter-specific issues that did not prevent the other adapters from being queried.</param>
public sealed record WingDiscoveryResult(
    IReadOnlyList<DiscoveredWing> Wings,
    IReadOnlyList<WingDiscoveryIssue> Issues);
