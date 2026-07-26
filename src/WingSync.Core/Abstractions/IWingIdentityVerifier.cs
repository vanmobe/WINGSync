using WingSync.Core.Domain;

namespace WingSync.Core.Abstractions;

/// <summary>
/// Re-discovers an explicitly configured endpoint and verifies that the physical serial
/// still matches before a connection epoch may write.
/// </summary>
public interface IWingIdentityVerifier
{
    /// <summary>
    /// Returns the fresh discovery identity or throws when the address is missing,
    /// ambiguous, or does not match the pinned serial.
    /// </summary>
    Task<DiscoveredWing> VerifyAsync(
        WingEndpoint endpoint,
        CancellationToken cancellationToken);
}

/// <summary>Identity verifier for deterministic simulator sessions only.</summary>
public sealed class StaticWingIdentityVerifier : IWingIdentityVerifier
{
    private readonly Dictionary<string, DiscoveredWing> identities;

    /// <summary>Initializes the verifier from fixed IP-keyed identities.</summary>
    public StaticWingIdentityVerifier(IEnumerable<DiscoveredWing> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        this.identities = identities.ToDictionary(
            static identity => identity.IpAddress,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<DiscoveredWing> VerifyAsync(
        WingEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!identities.TryGetValue(endpoint.IpAddress, out var identity))
        {
            throw new InvalidOperationException($"Geen WING-identiteit voor {endpoint.IpAddress}.");
        }

        var issues = ConfigValidator.ValidateDiscoveredIdentity(endpoint, identity, nameof(endpoint));
        if (issues.Any(static issue => issue.Severity == ValidationSeverity.Error))
        {
            throw new InvalidOperationException(string.Join(" ", issues.Select(static issue => issue.Message)));
        }

        return Task.FromResult(identity);
    }
}
