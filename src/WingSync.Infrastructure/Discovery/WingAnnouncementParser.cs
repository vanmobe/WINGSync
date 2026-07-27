using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Discovery;

/// <summary>
/// Parses the documented UDP response to the five-byte <c>WING?</c> discovery probe.
/// </summary>
public static class WingAnnouncementParser
{
    private const int MaximumAnnouncementLength = 512;
    private const int MaximumNameLength = 64;
    private const int MaximumModelLength = 64;
    private const int MaximumSerialLength = 96;
    private const int MaximumFirmwareLength = 160;

    /// <summary>
    /// Attempts to parse and validate a WING discovery announcement.
    /// </summary>
    /// <param name="payload">The exact UDP payload returned by the console.</param>
    /// <param name="remoteEndpoint">The source endpoint of the datagram.</param>
    /// <param name="discoveredAt">The UTC timestamp associated with receipt of the datagram.</param>
    /// <param name="wing">The parsed console when validation succeeds.</param>
    /// <returns><see langword="true"/> only when the complete announcement is valid.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        IPEndPoint remoteEndpoint,
        DateTimeOffset discoveredAt,
        [NotNullWhen(true)] out DiscoveredWing? wing)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        wing = null;

        if (remoteEndpoint.AddressFamily != AddressFamily.InterNetwork
            || payload.IsEmpty
            || payload.Length > MaximumAnnouncementLength
            || !IsPrintableAscii(payload))
        {
            return false;
        }

        var announcement = Encoding.ASCII.GetString(payload);
        var fields = announcement.Split(',', StringSplitOptions.None);

        // Trust the datagram source, not merely advertised text: requiring the
        // two addresses to match prevents stale or forwarded announcements.
        if (fields.Length != 6
            || !string.Equals(fields[0], "WING", StringComparison.Ordinal)
            || !TryParseCanonicalIPv4(fields[1], out var advertisedAddress)
            || !advertisedAddress.Equals(remoteEndpoint.Address)
            || !IsValidDisplayName(fields[2])
            || !IsToken(fields[3], MaximumModelLength)
            || !IsToken(fields[4], MaximumSerialLength)
            || !IsFirmware(fields[5]))
        {
            return false;
        }

        wing = new DiscoveredWing(
            fields[1],
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            discoveredAt);

        return true;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item is < 0x20 or > 0x7e)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCanonicalIPv4(
        string value,
        [NotNullWhen(true)] out IPAddress? address)
    {
        if (!IPAddress.TryParse(value, out address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !string.Equals(address.ToString(), value, StringComparison.Ordinal)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast)
            || IsIPv4Multicast(address))
        {
            // Canonical text rejects ambiguous shorthand such as leading-zero
            // octets before the address is persisted as a console identity.
            address = null;
            return false;
        }

        return true;
    }

    private static bool IsIPv4Multicast(IPAddress address)
    {
        var firstOctet = address.GetAddressBytes()[0];
        return firstOctet is >= 224 and <= 239;
    }

    private static bool IsValidDisplayName(string value)
    {
        return value.Length <= MaximumNameLength;
    }

    private static bool IsToken(string value, int maximumLength)
    {
        if (value.Length is 0 || value.Length > maximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFirmware(string value)
    {
        return value.Length is > 0 and <= MaximumFirmwareLength;
    }
}
