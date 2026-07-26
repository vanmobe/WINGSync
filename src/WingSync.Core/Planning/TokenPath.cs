using System.Collections.ObjectModel;
using System.Globalization;
using WingSync.Core.Domain;

namespace WingSync.Core.Planning;

/// <summary>
/// Represents a validated canonical WING token descriptor split into path segments.
/// </summary>
public sealed class TokenPath : IEquatable<TokenPath>
{
    private readonly string canonical;
    private readonly ReadOnlyCollection<string> segments;

    private TokenPath(string canonical, string[] segments)
    {
        this.canonical = canonical;
        this.segments = Array.AsReadOnly(segments);
    }

    /// <summary>Gets the canonical path segments without the leading slash.</summary>
    public IReadOnlyList<string> Segments => segments;

    /// <summary>Gets the final path segment.</summary>
    public string Leaf => segments[^1];

    /// <summary>
    /// Gets whether any segment starts with <c>$</c>, which marks WING read-only or
    /// transient data that must never be synchronized.
    /// </summary>
    public bool IsReadOnly => segments.Any(static segment => segment.StartsWith('$'));

    /// <summary>Parses a canonical token descriptor.</summary>
    /// <param name="value">A descriptor beginning with <c>/</c>.</param>
    /// <returns>The validated token path.</returns>
    /// <exception cref="FormatException">The descriptor is empty or malformed.</exception>
    public static TokenPath Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new FormatException($"'{value}' is not a canonical WING token path.");
        }

        return result;
    }

    /// <summary>Attempts to parse a canonical token descriptor.</summary>
    /// <param name="value">A descriptor beginning with <c>/</c>.</param>
    /// <param name="result">The parsed path when successful.</param>
    /// <returns><see langword="true"/> when the descriptor is valid.</returns>
    public static bool TryParse(string? value, out TokenPath result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var canonical = value.Trim();
        if (canonical.Length < 2
            || canonical[0] != '/'
            || canonical[^1] == '/'
            || canonical.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var parsedSegments = canonical[1..].Split('/');
        if (parsedSegments.Length == 0
            || parsedSegments.Any(static segment =>
                segment.Length == 0
                || segment is "." or ".."
                || segment.Any(char.IsWhiteSpace)))
        {
            return false;
        }

        result = new TokenPath(canonical, parsedSegments);
        return true;
    }

    /// <summary>Attempts to identify the regular or auxiliary channel addressed by this path.</summary>
    /// <param name="kind">The addressed channel collection.</param>
    /// <param name="channel">The one-based channel number.</param>
    /// <returns><see langword="true"/> for paths beginning with <c>/ch/n</c> or <c>/aux/n</c>.</returns>
    public bool TryGetChannel(out WingChannelKind kind, out int channel)
    {
        kind = default;
        channel = default;
        if (segments.Count < 2
            || !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out channel))
        {
            return false;
        }

        if (segments[0].Equals("ch", StringComparison.OrdinalIgnoreCase))
        {
            kind = WingChannelKind.Input;
            return true;
        }

        if (segments[0].Equals("aux", StringComparison.OrdinalIgnoreCase))
        {
            kind = WingChannelKind.Aux;
            return true;
        }

        channel = default;
        return false;
    }

    /// <summary>Returns a copy with its addressed input or auxiliary channel replaced.</summary>
    /// <param name="channel">The new one-based channel number.</param>
    /// <returns>A canonical rewritten token path.</returns>
    /// <exception cref="InvalidOperationException">This path does not address a channel.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The new number is outside the protocol range.</exception>
    public TokenPath WithChannel(int channel)
    {
        if (!TryGetChannel(out var kind, out _))
        {
            throw new InvalidOperationException($"Token '{canonical}' does not address a channel.");
        }

        var maximum = kind == WingChannelKind.Input
            ? WingChannelLimits.LastInput
            : WingChannelLimits.LastAux;
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, maximum);

        var rewrittenSegments = segments.ToArray();
        rewrittenSegments[1] = channel.ToString(CultureInfo.InvariantCulture);
        return new TokenPath('/' + string.Join('/', rewrittenSegments), rewrittenSegments);
    }

    /// <summary>Gets whether this path starts with the supplied segment sequence.</summary>
    /// <param name="prefixSegments">Segments to match case-insensitively.</param>
    /// <returns><see langword="true"/> when every prefix segment matches.</returns>
    public bool StartsWith(params ReadOnlySpan<string> prefixSegments)
    {
        if (prefixSegments.Length > segments.Count)
        {
            return false;
        }

        for (var index = 0; index < prefixSegments.Length; index++)
        {
            if (!segments[index].Equals(prefixSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(TokenPath? other) =>
        other is not null
        && canonical.Equals(other.canonical, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TokenPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(canonical);

    /// <inheritdoc />
    public override string ToString() => canonical;
}
