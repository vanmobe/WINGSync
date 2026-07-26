using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

namespace WingSync.Core.Domain;

/// <summary>
/// Indicates whether a validation issue blocks synchronization.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>The configuration can run, but the operator should review the issue.</summary>
    Warning,

    /// <summary>The configuration must not start a synchronization session.</summary>
    Error,
}

/// <summary>
/// Stable machine-readable identifiers for configuration validation findings.
/// </summary>
public enum ValidationIssueCode
{
    /// <summary>No configuration was supplied.</summary>
    MissingConfiguration,

    /// <summary>An endpoint is not a literal usable IP address.</summary>
    InvalidIpAddress,

    /// <summary>An endpoint has an invalid TCP port.</summary>
    InvalidPort,

    /// <summary>An endpoint uses a port other than native WAPI port 2222.</summary>
    NonStandardPort,

    /// <summary>FOH and monitor resolve to the same endpoint.</summary>
    DuplicateEndpoint,

    /// <summary>An endpoint has no pinned hardware serial number.</summary>
    MissingSerialPin,

    /// <summary>A serial number contains unsupported characters.</summary>
    InvalidSerial,

    /// <summary>Both configured roles contain the same serial number.</summary>
    DuplicateSerial,

    /// <summary>The configured serial does not match a discovered console.</summary>
    SerialMismatch,

    /// <summary>The configured address does not match a discovered console.</summary>
    AddressMismatch,

    /// <summary>The selected synchronization direction cannot safely execute.</summary>
    UnsupportedDirection,

    /// <summary>The less usual monitor-to-FOH direction is selected.</summary>
    ReverseDirection,

    /// <summary>No synchronization scope is enabled.</summary>
    NoScopes,

    /// <summary>A scope value is not defined by this version of the application.</summary>
    UnknownScope,

    /// <summary>A scope appears more than once.</summary>
    DuplicateScope,

    /// <summary>A selected scope is blocked by the high-risk safety switch.</summary>
    HighRiskScopeBlocked,

    /// <summary>A channel mapping is outside the supported protocol range.</summary>
    InvalidChannel,

    /// <summary>A source channel is mapped more than once.</summary>
    DuplicateSourceChannel,

    /// <summary>Multiple sources target the same channel.</summary>
    DuplicateTargetChannel,

    /// <summary>No regular channel mapping was supplied.</summary>
    MissingInputMappings,

    /// <summary>The configured float tolerance is not safe or finite.</summary>
    InvalidFloatTolerance,

    /// <summary>The echo suppression duration is not safe.</summary>
    InvalidEchoWindow,

    /// <summary>Verification failures must stop all subsequent writes.</summary>
    UnsafeVerificationPolicy,

    /// <summary>The initial synchronization policy bypasses conservative operator review.</summary>
    UnsafeInitialSync,
}

/// <summary>
/// Describes one actionable configuration validation finding.
/// </summary>
/// <param name="Code">The stable issue code.</param>
/// <param name="Severity">Whether the issue blocks synchronization.</param>
/// <param name="Path">The configuration member associated with the issue.</param>
/// <param name="Message">A concise operator-facing explanation.</param>
public sealed record ValidationIssue(
    ValidationIssueCode Code,
    ValidationSeverity Severity,
    string Path,
    string Message);

/// <summary>
/// Contains the immutable result of validating one application configuration.
/// </summary>
public sealed class ConfigurationValidationResult
{
    private readonly ReadOnlyCollection<ValidationIssue> issues;

    /// <summary>Initializes a result and defensively copies its findings.</summary>
    /// <param name="issues">The validation findings.</param>
    public ConfigurationValidationResult(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        this.issues = Array.AsReadOnly(issues.ToArray());
    }

    /// <summary>Gets all findings in deterministic validation order.</summary>
    public IReadOnlyList<ValidationIssue> Issues => issues;

    /// <summary>Gets whether no error-level findings were produced.</summary>
    public bool IsValid => issues.All(static issue => issue.Severity != ValidationSeverity.Error);
}

/// <summary>
/// Validates two-console configuration, mapping uniqueness, and conservative safety policy.
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<SyncScope> HighRiskScopes =
    [
        SyncScope.Tags,
        SyncScope.Conn,
        SyncScope.Pre,
        SyncScope.Post,
        SyncScope.Main1,
        SyncScope.Main2,
        SyncScope.Main3,
        SyncScope.Main4,
        SyncScope.Send,
        SyncScope.Fdr,
        SyncScope.Mute,
        SyncScope.Config,
    ];

    /// <summary>Validates an application configuration.</summary>
    /// <param name="configuration">The configuration to validate.</param>
    /// <returns>A deterministic list of errors and warnings.</returns>
    public static ConfigurationValidationResult Validate(AppConfiguration? configuration)
    {
        if (configuration is null)
        {
            return new ConfigurationValidationResult(
            [
                Error(
                    ValidationIssueCode.MissingConfiguration,
                    "$",
                    "No configuration was supplied."),
            ]);
        }

        var issues = new List<ValidationIssue>();
        var liveWrites = !configuration.Safety.DryRun;
        ValidateEndpoint(configuration.Foh, nameof(configuration.Foh), liveWrites, issues);
        ValidateEndpoint(configuration.Monitor, nameof(configuration.Monitor), liveWrites, issues);
        ValidateEndpointPair(configuration.Foh, configuration.Monitor, issues);
        ValidateDirection(configuration.Direction, issues);
        ValidateInitialSync(configuration, issues);
        ValidateSafety(configuration.Safety, issues);
        ValidateScopes(configuration.Scopes, configuration.Safety, issues);
        ValidateMappings(configuration.Channels, issues);
        return new ConfigurationValidationResult(issues);
    }

    /// <summary>
    /// Validates that discovery resolved the configured endpoint to the expected physical console.
    /// </summary>
    /// <param name="configured">The configured and optionally serial-pinned endpoint.</param>
    /// <param name="discovered">The discovery reply being considered for the role.</param>
    /// <param name="path">The role path used in returned issues.</param>
    /// <returns>Identity mismatch findings; an empty list means the identity matches.</returns>
    public static IReadOnlyList<ValidationIssue> ValidateDiscoveredIdentity(
        WingEndpoint configured,
        DiscoveredWing discovered,
        string path)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var issues = new List<ValidationIssue>();
        if (!AddressesEqual(configured.IpAddress, discovered.IpAddress))
        {
            issues.Add(Error(
                ValidationIssueCode.AddressMismatch,
                $"{path}.{nameof(WingEndpoint.IpAddress)}",
                $"Discovered address '{discovered.IpAddress}' does not match configured address '{configured.IpAddress}'."));
        }

        if (!string.IsNullOrWhiteSpace(configured.ExpectedSerial)
            && !string.Equals(
                configured.ExpectedSerial.Trim(),
                discovered.SerialNumber.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(
                ValidationIssueCode.SerialMismatch,
                $"{path}.{nameof(WingEndpoint.ExpectedSerial)}",
                $"Console serial '{discovered.SerialNumber}' does not match the pinned serial."));
        }

        return Array.AsReadOnly(issues.ToArray());
    }

    /// <summary>Returns whether a scope is disabled by default due to routing or show-impact risk.</summary>
    /// <param name="scope">The scope to inspect.</param>
    /// <returns><see langword="true"/> for high-risk scopes.</returns>
    public static bool IsHighRiskScope(SyncScope scope) => HighRiskScopes.Contains(scope);

    private static void ValidateEndpoint(
        WingEndpoint endpoint,
        string path,
        bool liveWrites,
        List<ValidationIssue> issues)
    {
        if (!TryGetUsableAddress(endpoint.IpAddress, out var address))
        {
            issues.Add(Error(
                ValidationIssueCode.InvalidIpAddress,
                $"{path}.{nameof(endpoint.IpAddress)}",
                $"'{endpoint.IpAddress}' is not a usable literal console IP address."));
        }
        else if (IPAddress.IsLoopback(address))
        {
            issues.Add(Warning(
                ValidationIssueCode.InvalidIpAddress,
                $"{path}.{nameof(endpoint.IpAddress)}",
                "A loopback address is suitable for a simulator only."));
        }

        if (endpoint.Port is < 1 or > IPEndPoint.MaxPort)
        {
            issues.Add(Error(
                ValidationIssueCode.InvalidPort,
                $"{path}.{nameof(endpoint.Port)}",
                $"Port {endpoint.Port} is outside the valid TCP range."));
        }
        else if (endpoint.Port != WingEndpoint.DefaultPort)
        {
            issues.Add(Error(
                ValidationIssueCode.NonStandardPort,
                $"{path}.{nameof(endpoint.Port)}",
                $"Deze WAPI-adapter ondersteunt uitsluitend TCP port {WingEndpoint.DefaultPort}."));
        }

        if (string.IsNullOrWhiteSpace(endpoint.ExpectedSerial))
        {
            issues.Add(liveWrites
                ? Error(
                    ValidationIssueCode.MissingSerialPin,
                    $"{path}.{nameof(endpoint.ExpectedSerial)}",
                    "Live writes vereisen een vastgezet hardware-serienummer voor elke console.")
                : Warning(
                    ValidationIssueCode.MissingSerialPin,
                    $"{path}.{nameof(endpoint.ExpectedSerial)}",
                    "Zet deze rol vast op het ontdekte hardware-serienummer vóór live writes."));
        }
        else if (!IsValidSerial(endpoint.ExpectedSerial))
        {
            issues.Add(Error(
                ValidationIssueCode.InvalidSerial,
                $"{path}.{nameof(endpoint.ExpectedSerial)}",
                "A serial may contain only letters, digits, '.', '-', and '_'."));
        }
    }

    private static void ValidateEndpointPair(
        WingEndpoint foh,
        WingEndpoint monitor,
        List<ValidationIssue> issues)
    {
        if (foh.Port == monitor.Port && AddressesEqual(foh.IpAddress, monitor.IpAddress))
        {
            issues.Add(Error(
                ValidationIssueCode.DuplicateEndpoint,
                $"{nameof(AppConfiguration.Foh)},{nameof(AppConfiguration.Monitor)}",
                "FOH and monitor must resolve to different console endpoints."));
        }

        if (!string.IsNullOrWhiteSpace(foh.ExpectedSerial)
            && !string.IsNullOrWhiteSpace(monitor.ExpectedSerial)
            && string.Equals(
                foh.ExpectedSerial.Trim(),
                monitor.ExpectedSerial.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(
                ValidationIssueCode.DuplicateSerial,
                $"{nameof(AppConfiguration.Foh)},{nameof(AppConfiguration.Monitor)}",
                "FOH and monitor cannot be pinned to the same hardware serial."));
        }
    }

    private static void ValidateDirection(
        SyncDirection direction,
        List<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(direction)
            || direction is SyncDirection.Disabled or SyncDirection.Bidirectional)
        {
            issues.Add(Error(
                ValidationIssueCode.UnsupportedDirection,
                nameof(AppConfiguration.Direction),
                "Choose one authoritative one-way synchronization direction."));
            return;
        }

        if (direction == SyncDirection.MonitorToFoh)
        {
            issues.Add(Warning(
                ValidationIssueCode.ReverseDirection,
                nameof(AppConfiguration.Direction),
                "Monitor-to-FOH synchronization is valid but can alter the audience mix."));
        }
    }

    private static void ValidateInitialSync(
        AppConfiguration configuration,
        List<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(configuration.InitialSync))
        {
            issues.Add(Error(
                ValidationIssueCode.UnsafeInitialSync,
                nameof(configuration.InitialSync),
                "The initial synchronization policy is unknown."));
            return;
        }

        if (configuration.InitialSync == InitialSync.SourceWins
            && !configuration.Safety.DryRun)
        {
            issues.Add(Warning(
                ValidationIssueCode.UnsafeInitialSync,
                nameof(configuration.InitialSync),
                "SourceWins will apply a fresh full scoped diff without operator confirmation."));
        }
    }

    private static void ValidateSafety(
        SafetySettings safety,
        List<ValidationIssue> issues)
    {
        if (!safety.StopOnVerificationFailure)
        {
            issues.Add(Error(
                ValidationIssueCode.UnsafeVerificationPolicy,
                $"{nameof(AppConfiguration.Safety)}.{nameof(safety.StopOnVerificationFailure)}",
                "Verification failures must pause synchronization; continuing is not supported."));
        }

        if (!safety.DryRun && !safety.RequireReadback)
        {
            issues.Add(Error(
                ValidationIssueCode.UnsafeVerificationPolicy,
                $"{nameof(AppConfiguration.Safety)}.{nameof(safety.RequireReadback)}",
                "Iedere live write moet via readback worden geverifieerd."));
        }

        if (!float.IsFinite(safety.FloatTolerance)
            || safety.FloatTolerance <= 0F
            || safety.FloatTolerance > 0.1F)
        {
            issues.Add(Error(
                ValidationIssueCode.InvalidFloatTolerance,
                $"{nameof(AppConfiguration.Safety)}.{nameof(safety.FloatTolerance)}",
                "Float tolerance must be finite, greater than zero, and at most 0.1."));
        }

        if (safety.EchoSuppressionWindow <= TimeSpan.Zero
            || safety.EchoSuppressionWindow > TimeSpan.FromMinutes(1))
        {
            issues.Add(Error(
                ValidationIssueCode.InvalidEchoWindow,
                $"{nameof(AppConfiguration.Safety)}.{nameof(safety.EchoSuppressionWindow)}",
                "Echo suppression must be greater than zero and at most one minute."));
        }
    }

    private static void ValidateScopes(
        IReadOnlyList<SyncScope> scopes,
        SafetySettings safety,
        List<ValidationIssue> issues)
    {
        if (scopes.Count == 0)
        {
            issues.Add(Error(
                ValidationIssueCode.NoScopes,
                nameof(AppConfiguration.Scopes),
                "Enable at least one synchronization scope."));
            return;
        }

        var seen = new HashSet<SyncScope>();
        for (var index = 0; index < scopes.Count; index++)
        {
            var scope = scopes[index];
            var path = $"{nameof(AppConfiguration.Scopes)}[{index}]";
            if (!Enum.IsDefined(scope))
            {
                issues.Add(Error(
                    ValidationIssueCode.UnknownScope,
                    path,
                    $"Scope value {(int)scope} is not supported."));
                continue;
            }

            if (!seen.Add(scope))
            {
                issues.Add(Warning(
                    ValidationIssueCode.DuplicateScope,
                    path,
                    $"Scope '{scope}' is configured more than once."));
            }

            if (!safety.AllowHighRiskWrites && IsHighRiskScope(scope))
            {
                issues.Add(Warning(
                    ValidationIssueCode.HighRiskScopeBlocked,
                    path,
                    $"Scope '{scope}' remains preview-only until high-risk writes are explicitly enabled."));
            }
        }
    }

    private static void ValidateMappings(
        ChannelMapping mappings,
        List<ValidationIssue> issues)
    {
        if (mappings.InputChannels.Count == 0)
        {
            issues.Add(Warning(
                ValidationIssueCode.MissingInputMappings,
                $"{nameof(AppConfiguration.Channels)}.{nameof(mappings.InputChannels)}",
                "No regular input channels will be synchronized."));
        }

        ValidateMappingCollection(
            mappings.InputChannels,
            WingChannelLimits.FirstInput,
            WingChannelLimits.LastInput,
            $"{nameof(AppConfiguration.Channels)}.{nameof(mappings.InputChannels)}",
            static mapping => mapping.Source,
            static mapping => mapping.Target,
            issues);

        ValidateMappingCollection(
            mappings.AuxChannels,
            WingChannelLimits.FirstAux,
            WingChannelLimits.LastAux,
            $"{nameof(AppConfiguration.Channels)}.{nameof(mappings.AuxChannels)}",
            static mapping => mapping.Source,
            static mapping => mapping.Target,
            issues);
    }

    private static void ValidateMappingCollection<T>(
        IReadOnlyList<T> mappings,
        int minimum,
        int maximum,
        string path,
        Func<T, int> sourceSelector,
        Func<T, int> targetSelector,
        List<ValidationIssue> issues)
    {
        var sources = new HashSet<int>();
        var targets = new HashSet<int>();

        for (var index = 0; index < mappings.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var mapping = mappings[index];
            if (mapping is null)
            {
                issues.Add(Error(
                    ValidationIssueCode.InvalidChannel,
                    itemPath,
                    "A channel mapping entry cannot be null."));
                continue;
            }

            var source = sourceSelector(mapping);
            var target = targetSelector(mapping);

            if (source < minimum || source > maximum)
            {
                issues.Add(Error(
                    ValidationIssueCode.InvalidChannel,
                    $"{itemPath}.Source",
                    $"Source channel {source} is outside the range {minimum}..{maximum}."));
            }

            if (target < minimum || target > maximum)
            {
                issues.Add(Error(
                    ValidationIssueCode.InvalidChannel,
                    $"{itemPath}.Target",
                    $"Target channel {target} is outside the range {minimum}..{maximum}."));
            }

            if (!sources.Add(source))
            {
                issues.Add(Error(
                    ValidationIssueCode.DuplicateSourceChannel,
                    $"{itemPath}.Source",
                    $"Source channel {source} is mapped more than once."));
            }

            if (!targets.Add(target))
            {
                issues.Add(Error(
                    ValidationIssueCode.DuplicateTargetChannel,
                    $"{itemPath}.Target",
                    $"Target channel {target} is used more than once."));
            }
        }
    }

    private static bool TryGetUsableAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text)
            || !IPAddress.TryParse(text.Trim(), out var parsed)
            || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.IPv6Any)
            || parsed.Equals(IPAddress.None)
            || parsed.Equals(IPAddress.IPv6None)
            || parsed.IsIPv6Multicast
            || IsIpv4Multicast(parsed)
            || parsed.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool IsIpv4Multicast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var firstOctet = address.GetAddressBytes()[0];
        return firstOctet is >= 224 and <= 239;
    }

    private static bool AddressesEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return IPAddress.TryParse(left, out var leftAddress)
            && IPAddress.TryParse(right, out var rightAddress)
                ? leftAddress.Equals(rightAddress)
                : string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidSerial(string serial)
    {
        var trimmed = serial.Trim();
        return trimmed.Length > 0
            && trimmed.All(static character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_');
    }

    private static ValidationIssue Error(
        ValidationIssueCode code,
        string path,
        string message) =>
        new(code, ValidationSeverity.Error, path, message);

    private static ValidationIssue Warning(
        ValidationIssueCode code,
        string path,
        string message) =>
        new(code, ValidationSeverity.Warning, path, message);
}
