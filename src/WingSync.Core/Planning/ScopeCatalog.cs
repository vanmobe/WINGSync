using System.Collections.ObjectModel;
using System.Globalization;
using WingSync.Core.Domain;

namespace WingSync.Core.Planning;

/// <summary>
/// Explains why a token or referenced channel could not be rewritten safely.
/// </summary>
public enum TokenRewriteStatus
{
    /// <summary>The path and any embedded reference were rewritten successfully.</summary>
    Rewritten,

    /// <summary>The token contains a read-only <c>$</c> segment.</summary>
    ReadOnlyToken,

    /// <summary>The token is outside all supported scope definitions.</summary>
    UnsupportedToken,

    /// <summary>No unique mapping exists for the source channel.</summary>
    MissingChannelMapping,

    /// <summary>A sidechain channel reference cannot be mapped safely.</summary>
    UnresolvedSidechainReference,
}

/// <summary>
/// Contains the result of rewriting a source-console token and its value for the target console.
/// </summary>
/// <param name="Status">The rewrite outcome.</param>
/// <param name="Scope">The matched scope, when any.</param>
/// <param name="TargetPath">The rewritten target path on success.</param>
/// <param name="TargetValue">The rewritten value on success.</param>
/// <param name="Message">An actionable explanation for a failed rewrite.</param>
public sealed record TokenRewriteResult(
    TokenRewriteStatus Status,
    SyncScope? Scope,
    TokenPath? TargetPath,
    WingValue? TargetValue,
    string? Message)
{
    /// <summary>Gets whether the token is safe and complete enough to plan.</summary>
    public bool IsSuccess => Status == TokenRewriteStatus.Rewritten;
}

/// <summary>
/// Describes one node that should be queried to establish a fresh scoped snapshot.
/// </summary>
/// <param name="Path">The canonical WING node or leaf path.</param>
/// <param name="ServedScopes">All requested scopes whose values can arrive from this node.</param>
public sealed record SnapshotNode(
    TokenPath Path,
    IReadOnlyList<SyncScope> ServedScopes);

/// <summary>
/// Matches official WING library scopes to canonical token families and snapshot nodes.
/// </summary>
/// <remarks>
/// Physical preamp paths under <c>/io/in</c> are intentionally absent. The WING <c>IN</c>
/// scope contains channel trim, balance, and phase only; it never grants permission to
/// change head-amplifier gain or phantom power.
/// </remarks>
public static class ScopeCatalog
{
    private static readonly ReadOnlyCollection<SyncScope> AllScopes =
        Array.AsReadOnly(Enum.GetValues<SyncScope>());

    private static readonly HashSet<string> CustomLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "name", "col", "icon", "led" };

    private static readonly HashSet<string> ConnectionLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "grp", "in", "altgrp", "altin" };

    private static readonly HashSet<string> InputSelectionLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "srcauto", "altsrc" };

    private static readonly HashSet<string> InputLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "inv", "trim", "bal" };

    private static readonly HashSet<string> DelayLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "dlymode", "dly", "dlyon" };

    private static readonly HashSet<string> PreInsertLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "on", "ins" };

    private static readonly HashSet<string> PostInsertLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "on", "mode", "ins", "w" };

    private static readonly HashSet<string> PanLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "pan", "wid", "tapwid" };

    private static readonly HashSet<string> MainLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "on", "lvl", "pre" };

    private static readonly HashSet<string> SendLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "on", "lvl", "pon", "mode", "plink", "pan" };

    private static readonly HashSet<string> ConfigurationLeaves =
        new(StringComparer.OrdinalIgnoreCase) { "proc", "ptap", "solosafe", "mon" };

    /// <summary>Gets every scope understood by the catalog.</summary>
    public static IReadOnlyList<SyncScope> SupportedScopes => AllScopes;

    /// <summary>Determines the official scope containing a writable token.</summary>
    /// <param name="path">The canonical token path.</param>
    /// <param name="scope">The matched scope.</param>
    /// <returns><see langword="true"/> when the token belongs to a supported scope.</returns>
    public static bool TryMatch(TokenPath path, out SyncScope scope)
    {
        ArgumentNullException.ThrowIfNull(path);
        scope = default;
        if (path.IsReadOnly
            || !path.TryGetChannel(out var kind, out var channel)
            || !IsValidChannel(kind, channel)
            || path.Segments.Count < 3)
        {
            return false;
        }

        var segments = path.Segments;
        var section = segments[2];

        // Three-segment channel leaves are controls directly on the channel.
        // Deeper paths are matched by their processing subtree below.
        if (segments.Count == 3)
        {
            if (CustomLeaves.Contains(section))
            {
                scope = SyncScope.Cust;
                return true;
            }

            if (section.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                scope = SyncScope.Tags;
                return true;
            }

            if (PanLeaves.Contains(section)
                && (kind == WingChannelKind.Input
                    || !section.Equals("tapwid", StringComparison.OrdinalIgnoreCase)))
            {
                scope = SyncScope.Pan;
                return true;
            }

            if (section.Equals("fdr", StringComparison.OrdinalIgnoreCase))
            {
                scope = SyncScope.Fdr;
                return true;
            }

            if (section.Equals("mute", StringComparison.OrdinalIgnoreCase))
            {
                scope = SyncScope.Mute;
                return true;
            }

            if (ConfigurationLeaves.Contains(section)
                && (kind == WingChannelKind.Input
                    || section.Equals("solosafe", StringComparison.OrdinalIgnoreCase)
                    || section.Equals("mon", StringComparison.OrdinalIgnoreCase)))
            {
                scope = SyncScope.Config;
                return true;
            }

            return false;
        }

        if (section.Equals("in", StringComparison.OrdinalIgnoreCase))
        {
            return TryMatchInputSubtree(segments, out scope);
        }

        if (kind == WingChannelKind.Input
            && section.Equals("flt", StringComparison.OrdinalIgnoreCase))
        {
            scope = SyncScope.Filter;
            return true;
        }

        if (kind == WingChannelKind.Input
            && (section.Equals("gate", StringComparison.OrdinalIgnoreCase)
                || section.Equals("gatesc", StringComparison.OrdinalIgnoreCase)))
        {
            scope = SyncScope.Gate;
            return true;
        }

        if (section.Equals("dyn", StringComparison.OrdinalIgnoreCase)
            || section.Equals("dynsc", StringComparison.OrdinalIgnoreCase)
            || (kind == WingChannelKind.Input
                && section.Equals("dynxo", StringComparison.OrdinalIgnoreCase)))
        {
            scope = SyncScope.Dyn;
            return true;
        }

        if (section.Equals("preins", StringComparison.OrdinalIgnoreCase)
            && segments.Count == 4
            && PreInsertLeaves.Contains(segments[3]))
        {
            scope = SyncScope.Pre;
            return true;
        }

        if (kind == WingChannelKind.Input
            && section.Equals("postins", StringComparison.OrdinalIgnoreCase)
            && segments.Count == 4
            && PostInsertLeaves.Contains(segments[3]))
        {
            scope = SyncScope.Post;
            return true;
        }

        if (section.Equals("eq", StringComparison.OrdinalIgnoreCase)
            || (kind == WingChannelKind.Input
                && section.Equals("peq", StringComparison.OrdinalIgnoreCase)))
        {
            scope = SyncScope.Eq;
            return true;
        }

        if (section.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return TryMatchMain(segments, out scope);
        }

        if (section.Equals("send", StringComparison.OrdinalIgnoreCase)
            && segments.Count == 5
            && IsSendDestination(segments[3])
            && SendLeaves.Contains(segments[4]))
        {
            scope = SyncScope.Send;
            return true;
        }

        return false;
    }

    /// <summary>Gets whether a writable token belongs to the supplied scope.</summary>
    /// <param name="scope">The expected scope.</param>
    /// <param name="path">The canonical token path.</param>
    /// <returns><see langword="true"/> only for an exact catalog classification.</returns>
    public static bool Matches(SyncScope scope, TokenPath path) =>
        TryMatch(path, out var actualScope) && actualScope == scope;

    /// <summary>
    /// Rewrites an eligible source token, its channel number, and an optional sidechain
    /// channel reference for the target console.
    /// </summary>
    /// <param name="sourcePath">The source-console token.</param>
    /// <param name="sourceValue">The source-console value.</param>
    /// <param name="mapping">The validated regular and auxiliary channel mapping.</param>
    /// <returns>A success result or a fail-closed reason.</returns>
    public static TokenRewriteResult Rewrite(
        TokenPath sourcePath,
        WingValue sourceValue,
        ChannelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(mapping);

        if (sourcePath.IsReadOnly)
        {
            return Failure(
                TokenRewriteStatus.ReadOnlyToken,
                default,
                "Read-only '$' tokens are never synchronized.");
        }

        if (!TryMatch(sourcePath, out var scope))
        {
            return Failure(
                TokenRewriteStatus.UnsupportedToken,
                default,
                $"Token '{sourcePath}' is outside the supported scope catalog.");
        }

        _ = sourcePath.TryGetChannel(out var kind, out var sourceChannel);
        var pathMapped = kind == WingChannelKind.Input
            ? mapping.TryMapInput(sourceChannel, out var targetChannel)
            : mapping.TryMapAux(sourceChannel, out targetChannel);
        if (!pathMapped)
        {
            return Failure(
                TokenRewriteStatus.MissingChannelMapping,
                scope,
                $"No unique {kind} mapping exists for source channel {sourceChannel}.");
        }

        var targetPath = sourcePath.WithChannel(targetChannel);
        var targetValue = sourceValue;
        if (IsSidechainSourceToken(sourcePath)
            && !SidechainReferenceMapper.TryRewrite(
                sourceValue,
                mapping,
                out targetValue,
                out var referenceError))
        {
            return Failure(
                TokenRewriteStatus.UnresolvedSidechainReference,
                scope,
                referenceError ?? "The sidechain channel reference could not be mapped.");
        }

        return new TokenRewriteResult(
            TokenRewriteStatus.Rewritten,
            scope,
            targetPath,
            targetValue,
            default);
    }

    /// <summary>Gets minimal nodes needed to snapshot selected scopes for one channel.</summary>
    /// <param name="scopes">The selected global WING scopes.</param>
    /// <param name="kind">Regular or auxiliary channel collection.</param>
    /// <param name="channel">The source or target channel number to query.</param>
    /// <returns>Deduplicated canonical nodes with the scopes they serve.</returns>
    public static IReadOnlyList<SnapshotNode> GetSnapshotNodes(
        IEnumerable<SyncScope> scopes,
        WingChannelKind kind,
        int channel)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ValidateChannel(kind, channel);

        var prefix = kind == WingChannelKind.Input ? "ch" : "aux";
        var scopeList = scopes.Distinct().ToArray();
        var nodes = new Dictionary<string, List<SyncScope>>(StringComparer.Ordinal);

        foreach (var scope in scopeList)
        {
            if (!Enum.IsDefined(scope))
            {
                continue;
            }

            foreach (var suffix in GetSnapshotSuffixes(scope, kind))
            {
                var canonical = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/{prefix}/{channel}/{suffix}");
                if (!nodes.TryGetValue(canonical, out var servedScopes))
                {
                    servedScopes = [];
                    nodes.Add(canonical, servedScopes);
                }

                servedScopes.Add(scope);
            }
        }

        return Array.AsReadOnly(
            nodes
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new SnapshotNode(
                    TokenPath.Parse(pair.Key),
                    Array.AsReadOnly(pair.Value.Distinct().ToArray())))
                .ToArray());
    }

    /// <summary>
    /// Gets source-side snapshot nodes for every mapped regular and auxiliary channel.
    /// </summary>
    /// <param name="scopes">The selected global WING scopes.</param>
    /// <param name="mapping">Mapping whose source channels should be observed.</param>
    /// <returns>Deduplicated source snapshot nodes in canonical path order.</returns>
    public static IReadOnlyList<SnapshotNode> GetSourceSnapshotNodes(
        IEnumerable<SyncScope> scopes,
        ChannelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(mapping);
        var scopeArray = scopes.Distinct().ToArray();
        var result = new List<SnapshotNode>();

        foreach (var channel in mapping.InputChannels.Select(static item => item.Source).Distinct())
        {
            result.AddRange(GetSnapshotNodes(scopeArray, WingChannelKind.Input, channel));
        }

        foreach (var channel in mapping.AuxChannels.Select(static item => item.Source).Distinct())
        {
            result.AddRange(GetSnapshotNodes(scopeArray, WingChannelKind.Aux, channel));
        }

        return Array.AsReadOnly(
            result
                .GroupBy(static item => item.Path)
                .Select(static group => new SnapshotNode(
                    group.Key,
                    Array.AsReadOnly(group.SelectMany(static item => item.ServedScopes).Distinct().ToArray())))
                .OrderBy(static item => item.Path.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>Gets whether the token contains a channel-valued gate or dynamics sidechain source.</summary>
    /// <param name="path">The token to inspect.</param>
    /// <returns><see langword="true"/> for <c>gatesc/src</c> and <c>dynsc/src</c>.</returns>
    public static bool IsSidechainSourceToken(TokenPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Segments;
        return segments.Count == 4
            && segments[3].Equals("src", StringComparison.OrdinalIgnoreCase)
            && (segments[2].Equals("gatesc", StringComparison.OrdinalIgnoreCase)
                || segments[2].Equals("dynsc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryMatchInputSubtree(
        IReadOnlyList<string> segments,
        out SyncScope scope)
    {
        scope = default;
        if (segments.Count == 5
            && segments[3].Equals("conn", StringComparison.OrdinalIgnoreCase)
            && ConnectionLeaves.Contains(segments[4]))
        {
            scope = SyncScope.Conn;
            return true;
        }

        if (segments.Count != 5
            || !segments[3].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (InputSelectionLeaves.Contains(segments[4]))
        {
            scope = SyncScope.Conn;
            return true;
        }

        if (InputLeaves.Contains(segments[4]))
        {
            scope = SyncScope.In;
            return true;
        }

        if (DelayLeaves.Contains(segments[4]))
        {
            scope = SyncScope.Delay;
            return true;
        }

        return false;
    }

    private static bool TryMatchMain(
        IReadOnlyList<string> segments,
        out SyncScope scope)
    {
        scope = default;
        if (segments.Count != 5
            || !int.TryParse(
                segments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var main)
            || main is < 1 or > 4
            || !MainLeaves.Contains(segments[4]))
        {
            return false;
        }

        scope = main switch
        {
            1 => SyncScope.Main1,
            2 => SyncScope.Main2,
            3 => SyncScope.Main3,
            _ => SyncScope.Main4,
        };
        return true;
    }

    private static bool IsSendDestination(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bus))
        {
            return bus is >= 1 and <= 16;
        }

        var matrixNumber = value.StartsWith("MX.", StringComparison.OrdinalIgnoreCase)
            ? value[3..]
            : value.StartsWith("MX", StringComparison.OrdinalIgnoreCase)
                ? value[2..]
                : string.Empty;
        return int.TryParse(
                matrixNumber,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var matrix)
            && matrix is >= 1 and <= 8;
    }

    private static IEnumerable<string> GetSnapshotSuffixes(
        SyncScope scope,
        WingChannelKind kind) =>
        kind switch
        {
            WingChannelKind.Input => GetInputSnapshotSuffixes(scope),
            WingChannelKind.Aux => GetAuxSnapshotSuffixes(scope),
            _ => [],
        };

    private static IEnumerable<string> GetInputSnapshotSuffixes(SyncScope scope) =>
        scope switch
        {
            SyncScope.Cust => ["name", "col", "icon", "led"],
            SyncScope.Tags => ["tags"],
            SyncScope.Conn => ["in/conn", "in/set"],
            SyncScope.In => ["in/set"],
            SyncScope.Filter => ["flt"],
            SyncScope.Delay => ["in/set"],
            SyncScope.Gate => ["gate", "gatesc"],
            SyncScope.Dyn => ["dyn", "dynxo", "dynsc"],
            SyncScope.Pre => ["preins"],
            SyncScope.Post => ["postins"],
            SyncScope.Eq => ["eq", "peq"],
            SyncScope.Pan => ["pan", "wid", "tapwid"],
            SyncScope.Main1 => ["main/1"],
            SyncScope.Main2 => ["main/2"],
            SyncScope.Main3 => ["main/3"],
            SyncScope.Main4 => ["main/4"],
            SyncScope.Send => ["send"],
            SyncScope.Fdr => ["fdr"],
            SyncScope.Mute => ["mute"],
            SyncScope.Config => ["proc", "ptap", "solosafe", "mon"],
            _ => [],
        };

    // WAPI 3.1 AUX channels deliberately have a smaller processing tree than
    // regular input channels. In particular, they have no FLT, GATE/GATESC,
    // DYNXO, POSTINS, PEQ, TAPWID, PROC, or PTAP nodes. Keep this catalog
    // separate so a global scope selection can never manufacture such nodes.
    private static IEnumerable<string> GetAuxSnapshotSuffixes(SyncScope scope) =>
        scope switch
        {
            SyncScope.Cust => ["name", "col", "icon", "led"],
            SyncScope.Tags => ["tags"],
            SyncScope.Conn => ["in/conn", "in/set"],
            SyncScope.In => ["in/set"],
            SyncScope.Delay => ["in/set"],
            SyncScope.Dyn => ["dyn", "dynsc"],
            SyncScope.Pre => ["preins"],
            SyncScope.Eq => ["eq"],
            SyncScope.Pan => ["pan", "wid"],
            SyncScope.Main1 => ["main/1"],
            SyncScope.Main2 => ["main/2"],
            SyncScope.Main3 => ["main/3"],
            SyncScope.Main4 => ["main/4"],
            SyncScope.Send => ["send"],
            SyncScope.Fdr => ["fdr"],
            SyncScope.Mute => ["mute"],
            SyncScope.Config => ["solosafe", "mon"],
            _ => [],
        };

    private static bool IsValidChannel(WingChannelKind kind, int channel) =>
        kind switch
        {
            WingChannelKind.Input =>
                channel is >= WingChannelLimits.FirstInput and <= WingChannelLimits.LastInput,
            WingChannelKind.Aux =>
                channel is >= WingChannelLimits.FirstAux and <= WingChannelLimits.LastAux,
            _ => false,
        };

    private static void ValidateChannel(WingChannelKind kind, int channel)
    {
        if (!Enum.IsDefined(kind) || !IsValidChannel(kind, channel))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                $"Channel is outside the supported {kind} range.");
        }
    }

    private static TokenRewriteResult Failure(
        TokenRewriteStatus status,
        SyncScope? scope,
        string message) =>
        new(status, scope, default, default, message);
}

/// <summary>
/// Rewrites enumerated gate and dynamics sidechain channel references without touching
/// external, self, or off-source values.
/// </summary>
public static class SidechainReferenceMapper
{
    /// <summary>Attempts to rewrite a native sidechain source value.</summary>
    /// <param name="source">The original typed sidechain source.</param>
    /// <param name="mapping">The validated channel mapping.</param>
    /// <param name="target">The rewritten or unchanged target value.</param>
    /// <param name="error">An actionable error when a recognized channel reference has no mapping.</param>
    /// <returns>
    /// <see langword="false"/> only when the value explicitly references a regular or auxiliary
    /// channel for which no unique mapping exists.
    /// </returns>
    public static bool TryRewrite(
        WingValue source,
        ChannelMapping mapping,
        out WingValue target,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        target = source;
        error = default;

        if (source.Type == WingValueType.I)
        {
            // Native integer sidechain values encode regular input channels only;
            // values outside that range represent non-channel choices and pass through.
            var integerSourceChannel = source.AsInt32();
            if (integerSourceChannel is < WingChannelLimits.FirstInput or > WingChannelLimits.LastInput)
            {
                return true;
            }

            if (!mapping.TryMapInput(integerSourceChannel, out var integerTargetChannel))
            {
                if (mapping.InputChannels.Count > 0 &&
                    mapping.InputChannels.All(static item => item.Source == item.Target))
                {
                    target = source;
                    return true;
                }

                error = $"Sidechain input channel {integerSourceChannel} has no unique target mapping.";
                return false;
            }

            target = WingValue.FromInt32(integerTargetChannel);
            return true;
        }

        if (source.Type != WingValueType.S
            || !TryParseReference(
                source.AsString(),
                out var kind,
                out var sourceChannel,
                out var prefix,
                out var separator))
        {
            // External sources, self, off, and other named choices are semantic
            // values rather than channel addresses and must remain unchanged.
            return true;
        }

        var mapped = kind == WingChannelKind.Input
            ? mapping.TryMapInput(sourceChannel, out var targetChannel)
            : mapping.TryMapAux(sourceChannel, out targetChannel);
        if (!mapped)
        {
            var numberingIsIdentity = kind == WingChannelKind.Input
                ? mapping.InputChannels.Count > 0 &&
                  mapping.InputChannels.All(static item => item.Source == item.Target)
                : mapping.AuxChannels.Count > 0 &&
                  mapping.AuxChannels.All(static item => item.Source == item.Target);
            if (numberingIsIdentity)
            {
                target = source;
                return true;
            }

            error = $"Sidechain {kind} channel {sourceChannel} has no unique target mapping.";
            return false;
        }

        target = WingValue.FromString(
            string.Concat(
                prefix,
                separator,
                targetChannel.ToString(CultureInfo.InvariantCulture)));
        return true;
    }

    private static bool TryParseReference(
        string value,
        out WingChannelKind kind,
        out int channel,
        out string prefix,
        out string separator)
    {
        kind = default;
        channel = default;
        prefix = string.Empty;
        separator = string.Empty;
        var trimmed = value.Trim();

        if (TryParseWithPrefix(trimmed, "AUX", out channel, out separator))
        {
            kind = WingChannelKind.Aux;
            prefix = trimmed[..3];
            return channel is >= WingChannelLimits.FirstAux and <= WingChannelLimits.LastAux;
        }

        if (TryParseWithPrefix(trimmed, "CH", out channel, out separator))
        {
            kind = WingChannelKind.Input;
            prefix = trimmed[..2];
            return channel is >= WingChannelLimits.FirstInput and <= WingChannelLimits.LastInput;
        }

        return false;
    }

    private static bool TryParseWithPrefix(
        string value,
        string expectedPrefix,
        out int channel,
        out string separator)
    {
        channel = default;
        separator = string.Empty;
        if (!value.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || value.Length == expectedPrefix.Length)
        {
            return false;
        }

        var suffix = value[expectedPrefix.Length..];
        if (suffix[0] is '.' or ':' or '/')
        {
            separator = suffix[..1];
            suffix = suffix[1..];
        }

        return suffix.Length > 0
            && int.TryParse(
                suffix,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out channel);
    }
}
