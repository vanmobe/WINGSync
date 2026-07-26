using System.Collections.ObjectModel;

namespace WingSync.Core.Domain;

/// <summary>
/// Identifies the independently addressed WING channel collections.
/// </summary>
public enum WingChannelKind
{
    /// <summary>One of the forty regular input channels under <c>/ch</c>.</summary>
    Input,

    /// <summary>One of the eight auxiliary input channels under <c>/aux</c>.</summary>
    Aux,
}

/// <summary>
/// Defines protocol channel limits independently from product marketing terminology.
/// </summary>
public static class WingChannelLimits
{
    /// <summary>The first regular input channel.</summary>
    public const int FirstInput = 1;

    /// <summary>The last regular input channel exposed as <c>/ch</c>.</summary>
    public const int LastInput = 40;

    /// <summary>The first auxiliary input channel.</summary>
    public const int FirstAux = 1;

    /// <summary>The last auxiliary input channel exposed as <c>/aux</c>.</summary>
    public const int LastAux = 8;
}

/// <summary>
/// Maps one regular source input channel to one regular target input channel.
/// </summary>
/// <param name="Source">The source channel in the range 1 through 40.</param>
/// <param name="Target">The target channel in the range 1 through 40.</param>
public sealed record InputChannelMapping(int Source, int Target);

/// <summary>
/// Maps one auxiliary source input to one auxiliary target input.
/// </summary>
/// <param name="Source">The source auxiliary channel in the range 1 through 8.</param>
/// <param name="Target">The target auxiliary channel in the range 1 through 8.</param>
public sealed record AuxChannelMapping(int Source, int Target);

/// <summary>
/// Holds regular and auxiliary mappings as separate immutable collections.
/// </summary>
public sealed class ChannelMapping
{
    private readonly ReadOnlyCollection<InputChannelMapping> inputChannels;
    private readonly ReadOnlyCollection<AuxChannelMapping> auxChannels;

    /// <summary>
    /// Initializes channel mappings and defensively copies both collections.
    /// </summary>
    /// <param name="inputChannels">Regular input-channel mappings.</param>
    /// <param name="auxChannels">Auxiliary input mappings.</param>
    public ChannelMapping(
        IEnumerable<InputChannelMapping>? inputChannels = null,
        IEnumerable<AuxChannelMapping>? auxChannels = null)
    {
        this.inputChannels = Array.AsReadOnly((inputChannels ?? []).ToArray());
        this.auxChannels = Array.AsReadOnly((auxChannels ?? []).ToArray());
    }

    /// <summary>Gets the regular input-channel mappings.</summary>
    public IReadOnlyList<InputChannelMapping> InputChannels => inputChannels;

    /// <summary>Gets the auxiliary input mappings.</summary>
    public IReadOnlyList<AuxChannelMapping> AuxChannels => auxChannels;

    /// <summary>
    /// Creates an identity map for all forty regular channels and no auxiliary channels.
    /// </summary>
    /// <returns>A new immutable mapping.</returns>
    public static ChannelMapping IdentityInputs() =>
        new(Enumerable.Range(WingChannelLimits.FirstInput, WingChannelLimits.LastInput)
            .Select(static channel => new InputChannelMapping(channel, channel)));

    /// <summary>Attempts to map a regular source channel.</summary>
    /// <param name="source">The regular source channel.</param>
    /// <param name="target">The mapped target channel when successful.</param>
    /// <returns><see langword="true"/> when exactly one configured mapping was found.</returns>
    public bool TryMapInput(int source, out int target) =>
        TryMap(inputChannels, source, static item => item.Source, static item => item.Target, out target);

    /// <summary>Attempts to map an auxiliary source channel.</summary>
    /// <param name="source">The auxiliary source channel.</param>
    /// <param name="target">The mapped target channel when successful.</param>
    /// <returns><see langword="true"/> when exactly one configured mapping was found.</returns>
    public bool TryMapAux(int source, out int target) =>
        TryMap(auxChannels, source, static item => item.Source, static item => item.Target, out target);

    private static bool TryMap<T>(
        IReadOnlyList<T> mappings,
        int source,
        Func<T, int> sourceSelector,
        Func<T, int> targetSelector,
        out int target)
    {
        target = default;
        var found = false;

        foreach (var mapping in mappings)
        {
            if (sourceSelector(mapping) != source)
            {
                continue;
            }

            if (found)
            {
                target = default;
                return false;
            }

            target = targetSelector(mapping);
            found = true;
        }

        return found;
    }
}
