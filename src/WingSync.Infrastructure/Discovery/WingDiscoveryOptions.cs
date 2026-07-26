namespace WingSync.Infrastructure.Discovery;

/// <summary>
/// Configures a WING discovery pass.
/// </summary>
public sealed class WingDiscoveryOptions
{
    /// <summary>
    /// Gets or sets how long replies are collected after the probes have been sent.
    /// </summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMilliseconds(1_500);

    /// <summary>
    /// Gets or sets the UDP port used by the WING discovery protocol.
    /// </summary>
    public int Port { get; set; } = 2_222;

    /// <summary>
    /// Gets or sets whether every eligible adapter receives a probe to its directed broadcast address.
    /// </summary>
    public bool IncludeDirectedBroadcast { get; set; } = true;

    /// <summary>
    /// Gets or sets whether every eligible adapter receives a probe to 255.255.255.255.
    /// </summary>
    public bool IncludeGlobalBroadcast { get; set; } = true;

    internal void Validate()
    {
        if (ReplyTimeout <= TimeSpan.Zero || ReplyTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReplyTimeout),
                ReplyTimeout,
                "The reply timeout must be greater than zero and no more than one minute.");
        }

        if (Port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Port),
                Port,
                "The UDP port must be between 1 and 65535.");
        }

        if (!IncludeDirectedBroadcast && !IncludeGlobalBroadcast)
        {
            throw new InvalidOperationException(
                "At least one discovery broadcast mode must be enabled.");
        }
    }
}
