namespace WingSync.Core.Domain;

/// <summary>
/// Describes a configured WING TCP endpoint and its optional pinned serial number.
/// </summary>
/// <param name="IpAddress">A literal IPv4 or IPv6 address.</param>
/// <param name="Port">The native WAPI TCP port, normally 2222.</param>
/// <param name="ExpectedSerial">The serial number expected at this address.</param>
public sealed record WingEndpoint(
    string IpAddress,
    int Port = WingEndpoint.DefaultPort,
    string? ExpectedSerial = null)
{
    /// <summary>The native WING discovery and WAPI port.</summary>
    public const int DefaultPort = 2222;
}

/// <summary>
/// Represents one console identity returned by WING UDP discovery.
/// </summary>
/// <param name="IpAddress">The address advertised by the console.</param>
/// <param name="Name">The configured console name.</param>
/// <param name="Model">The advertised WING model.</param>
/// <param name="SerialNumber">The hardware serial number.</param>
/// <param name="FirmwareVersion">The advertised firmware version.</param>
/// <param name="DiscoveredAt">The local time at which the reply was received.</param>
public sealed record DiscoveredWing(
    string IpAddress,
    string Name,
    string Model,
    string SerialNumber,
    string FirmwareVersion,
    DateTimeOffset DiscoveredAt)
{
    /// <summary>Creates a serial-pinned endpoint for the discovered console.</summary>
    public WingEndpoint Endpoint => new(IpAddress, WingEndpoint.DefaultPort, SerialNumber);
}

/// <summary>
/// Resolves a discovery result without weakening an existing hardware-identity pin.
/// </summary>
public static class PinnedWingSelection
{
    /// <summary>
    /// Selects by serial whenever a serial is pinned. The IP address is used only
    /// for an endpoint that has never been pinned to a hardware identity.
    /// </summary>
    /// <param name="candidates">The identities returned by the latest discovery pass.</param>
    /// <param name="expectedSerial">The previously pinned serial number, when available.</param>
    /// <param name="fallbackIpAddress">The configured address for an unpinned endpoint.</param>
    /// <returns>The matching identity, or <see langword="null"/> when the pin is absent.</returns>
    public static DiscoveredWing? Find(
        IEnumerable<DiscoveredWing> candidates,
        string? expectedSerial,
        string? fallbackIpAddress)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (!string.IsNullOrWhiteSpace(expectedSerial))
        {
            return candidates.FirstOrDefault(candidate =>
                candidate.SerialNumber.Equals(
                    expectedSerial.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(fallbackIpAddress))
        {
            return null;
        }

        return candidates.FirstOrDefault(candidate =>
            candidate.IpAddress.Equals(
                fallbackIpAddress.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }
}
