using System.Net;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Discovery;

/// <summary>
/// Performs a fresh multi-adapter discovery pass before every writable connection epoch.
/// </summary>
public sealed class WingIdentityVerifier : IWingIdentityVerifier
{
    private readonly WingDiscoveryService discovery;
    private readonly WingDiscoveryOptions options;

    /// <summary>Initializes a verifier with a discovery service and optional timeout policy.</summary>
    public WingIdentityVerifier(
        WingDiscoveryService discovery,
        WingDiscoveryOptions? options = null)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.options = options ?? new WingDiscoveryOptions();
    }

    /// <inheritdoc />
    public async Task<DiscoveredWing> VerifyAsync(
        WingEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IPAddress.TryParse(endpoint.IpAddress, out var configuredAddress))
        {
            throw new InvalidOperationException($"'{endpoint.IpAddress}' is geen geldig IP-adres.");
        }

        var result = await discovery.DiscoverAsync(options, cancellationToken).ConfigureAwait(false);
        var matches = result.Wings
            .Where(wing =>
                IPAddress.TryParse(wing.IpAddress, out var discoveredAddress) &&
                discoveredAddress.Equals(configuredAddress))
            .ToArray();
        if (matches.Length == 0)
        {
            var issueSummary = result.Issues.Count == 0
                ? string.Empty
                : $" Netwerkdetails: {string.Join("; ", result.Issues.Select(static issue => issue.Message))}";
            throw new InvalidOperationException(
                $"Geen WING-discoveryantwoord ontvangen van {endpoint.IpAddress}.{issueSummary}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Meerdere WING-identiteiten adverteren adres {endpoint.IpAddress}.");
        }

        var identity = matches[0];
        var validation = ConfigValidator.ValidateDiscoveredIdentity(endpoint, identity, nameof(endpoint));
        var errors = validation
            .Where(static issue => issue.Severity == ValidationSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                string.Join(" ", errors.Select(static issue => issue.Message)));
        }

        return identity;
    }
}
