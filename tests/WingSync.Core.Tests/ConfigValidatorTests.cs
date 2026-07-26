using WingSync.Core.Domain;

namespace WingSync.Core.Tests;

internal static class ConfigValidatorTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Config.Safe configuration validates", SafeConfigurationValidates);
        suite.Add("Config.Null configuration is rejected", NullConfigurationIsRejected);
        suite.Add("Config.Unusable addresses are rejected", UnusableAddressesAreRejected);
        suite.Add("Config.Loopback address produces simulator warning", LoopbackProducesWarning);
        suite.Add("Config.Port policy is enforced", PortPolicyIsEnforced);
        suite.Add("Config.Serial pin policy is enforced", SerialPolicyIsEnforced);
        suite.Add("Config.Duplicate console identity is rejected", DuplicateIdentityIsRejected);
        suite.Add("Config.Direction policy is one-way", DirectionPolicyIsOneWay);
        suite.Add("Config.Initial source-wins warns when live", InitialSourceWinsWarnsWhenLive);
        suite.Add("Config.Scope policy rejects empty and unknown", ScopePolicyRejectsEmptyAndUnknown);
        suite.Add("Config.Scope duplicates and blocked risk warn", ScopeWarningsAreReported);
        suite.Add("Config.Mapping range and uniqueness are enforced", MappingPolicyIsEnforced);
        suite.Add("Config.Safety numeric bounds are enforced", SafetyBoundsAreEnforced);
        suite.Add("Config.Verification failures must stop synchronization", UnsafeVerificationPolicyIsRejected);
        suite.Add("Config.Discovery identity requires address and serial", DiscoveryIdentityIsChecked);
        suite.Add("Config.High-risk scope classification is complete", HighRiskClassificationIsComplete);
        suite.Add("Config.Validation findings use deterministic order", ValidationOrderIsDeterministic);
    }

    private static void SafeConfigurationValidates()
    {
        var result = ConfigValidator.Validate(CreateValidConfiguration());
        AssertEx.True(result.IsValid);
        AssertEx.Equal(0, result.Issues.Count);
    }

    private static void NullConfigurationIsRejected()
    {
        var result = ConfigValidator.Validate(null);
        AssertEx.False(result.IsValid);
        AssertCodes(result, ValidationIssueCode.MissingConfiguration);
    }

    private static void UnusableAddressesAreRejected()
    {
        string[] unusable =
        [
            string.Empty,
            "wing.local",
            "0.0.0.0",
            "255.255.255.255",
            "224.0.0.1",
            "::",
            "ff02::1",
        ];

        foreach (var address in unusable)
        {
            var result = ConfigValidator.Validate(CreateValidConfiguration(
                foh: new WingEndpoint(address, ExpectedSerial: "FOH-1")));
            AssertEx.False(result.IsValid, $"Address '{address}' unexpectedly validated.");
            AssertCodes(result, ValidationIssueCode.InvalidIpAddress);
        }
    }

    private static void LoopbackProducesWarning()
    {
        var result = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("127.0.0.1", ExpectedSerial: "FOH-1")));
        AssertEx.True(result.IsValid);
        var issue = SingleIssue(result, ValidationIssueCode.InvalidIpAddress);
        AssertEx.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    private static void PortPolicyIsEnforced()
    {
        var invalidLow = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("10.0.0.10", 0, "FOH-1")));
        AssertEx.False(invalidLow.IsValid);
        AssertCodes(invalidLow, ValidationIssueCode.InvalidPort);

        var invalidHigh = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("10.0.0.10", 65536, "FOH-1")));
        AssertEx.False(invalidHigh.IsValid);
        AssertCodes(invalidHigh, ValidationIssueCode.InvalidPort);

        var nonStandard = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("10.0.0.10", 2223, "FOH-1")));
        AssertEx.False(nonStandard.IsValid);
        AssertEx.Equal(
            ValidationSeverity.Error,
            SingleIssue(nonStandard, ValidationIssueCode.NonStandardPort).Severity);
    }

    private static void SerialPolicyIsEnforced()
    {
        var missing = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("10.0.0.10")));
        AssertEx.True(missing.IsValid);
        AssertEx.Equal(
            ValidationSeverity.Warning,
            SingleIssue(missing, ValidationIssueCode.MissingSerialPin).Severity);

        var missingLive = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("10.0.0.10"),
            safety: LiveSafety()));
        AssertEx.False(missingLive.IsValid);
        AssertEx.Equal(
            ValidationSeverity.Error,
            SingleIssue(missingLive, ValidationIssueCode.MissingSerialPin).Severity);

        string[] invalid = ["SERIAL 1", "SERIAL/1", "SÉRIAL"];
        foreach (var serial in invalid)
        {
            var result = ConfigValidator.Validate(CreateValidConfiguration(
                foh: new WingEndpoint("10.0.0.10", ExpectedSerial: serial)));
            AssertEx.False(result.IsValid);
            AssertCodes(result, ValidationIssueCode.InvalidSerial);
        }

        var allowed = ConfigValidator.Validate(CreateValidConfiguration(
            foh: new WingEndpoint("10.0.0.10", ExpectedSerial: "SERIAL_1-A.B")));
        AssertEx.True(allowed.IsValid);
    }

    private static void DuplicateIdentityIsRejected()
    {
        var duplicateEndpoint = ConfigValidator.Validate(CreateValidConfiguration(
            monitor: new WingEndpoint("10.0.0.10", ExpectedSerial: "MON-1")));
        AssertEx.False(duplicateEndpoint.IsValid);
        AssertCodes(duplicateEndpoint, ValidationIssueCode.DuplicateEndpoint);

        var duplicateSerial = ConfigValidator.Validate(CreateValidConfiguration(
            monitor: new WingEndpoint("10.0.0.11", ExpectedSerial: " foh-1 ")));
        AssertEx.False(duplicateSerial.IsValid);
        AssertCodes(duplicateSerial, ValidationIssueCode.DuplicateSerial);
    }

    private static void DirectionPolicyIsOneWay()
    {
        foreach (var rejected in new[]
                 {
                     SyncDirection.Disabled,
                     SyncDirection.Bidirectional,
                     (SyncDirection)999,
                 })
        {
            var result = ConfigValidator.Validate(CreateValidConfiguration(direction: rejected));
            AssertEx.False(result.IsValid);
            AssertCodes(result, ValidationIssueCode.UnsupportedDirection);
        }

        var reverse = ConfigValidator.Validate(CreateValidConfiguration(
            direction: SyncDirection.MonitorToFoh));
        AssertEx.True(reverse.IsValid);
        AssertEx.Equal(
            ValidationSeverity.Warning,
            SingleIssue(reverse, ValidationIssueCode.ReverseDirection).Severity);
    }

    private static void InitialSourceWinsWarnsWhenLive()
    {
        var live = ConfigValidator.Validate(CreateValidConfiguration(
            initialSync: InitialSync.SourceWins,
            safety: LiveSafety()));
        AssertEx.True(live.IsValid);
        AssertCodes(live, ValidationIssueCode.UnsafeInitialSync);

        var dryRun = ConfigValidator.Validate(CreateValidConfiguration(
            initialSync: InitialSync.SourceWins,
            safety: SafetySettings.SafeDefaults));
        AssertEx.DoesNotContain(
            dryRun.Issues.Select(static issue => issue.Code),
            ValidationIssueCode.UnsafeInitialSync);

        var unknown = ConfigValidator.Validate(CreateValidConfiguration(
            initialSync: (InitialSync)999));
        AssertEx.False(unknown.IsValid);
        AssertCodes(unknown, ValidationIssueCode.UnsafeInitialSync);
    }

    private static void ScopePolicyRejectsEmptyAndUnknown()
    {
        var empty = ConfigValidator.Validate(CreateValidConfiguration(scopes: []));
        AssertEx.False(empty.IsValid);
        AssertCodes(empty, ValidationIssueCode.NoScopes);

        var unknown = ConfigValidator.Validate(CreateValidConfiguration(
            scopes: [SyncScope.Eq, (SyncScope)999]));
        AssertEx.False(unknown.IsValid);
        AssertCodes(unknown, ValidationIssueCode.UnknownScope);
    }

    private static void ScopeWarningsAreReported()
    {
        var result = ConfigValidator.Validate(CreateValidConfiguration(
            scopes: [SyncScope.Eq, SyncScope.Eq, SyncScope.Mute]));

        AssertEx.True(result.IsValid);
        AssertCodes(
            result,
            ValidationIssueCode.DuplicateScope,
            ValidationIssueCode.HighRiskScopeBlocked);

        var allowed = ConfigValidator.Validate(CreateValidConfiguration(
            scopes: [SyncScope.Mute],
            safety: LiveSafety(allowHighRiskWrites: true)));
        AssertEx.True(allowed.IsValid);
        AssertEx.DoesNotContain(
            allowed.Issues.Select(static issue => issue.Code),
            ValidationIssueCode.HighRiskScopeBlocked);
    }

    private static void MappingPolicyIsEnforced()
    {
        var missingInputs = ConfigValidator.Validate(CreateValidConfiguration(
            channels: new ChannelMapping(
                auxChannels: [new AuxChannelMapping(1, 1)])));
        AssertEx.True(missingInputs.IsValid);
        AssertEx.Equal(
            ValidationSeverity.Warning,
            SingleIssue(missingInputs, ValidationIssueCode.MissingInputMappings).Severity);

        var invalid = ConfigValidator.Validate(CreateValidConfiguration(
            channels: new ChannelMapping(
            [
                new InputChannelMapping(0, 1),
                new InputChannelMapping(2, 41),
                new InputChannelMapping(2, 3),
                new InputChannelMapping(4, 3),
            ],
            [
                new AuxChannelMapping(9, 1),
                new AuxChannelMapping(2, 0),
            ])));

        AssertEx.False(invalid.IsValid);
        AssertCodes(
            invalid,
            ValidationIssueCode.InvalidChannel,
            ValidationIssueCode.DuplicateSourceChannel,
            ValidationIssueCode.DuplicateTargetChannel);
    }

    private static void SafetyBoundsAreEnforced()
    {
        foreach (var tolerance in new[] { 0F, -1F, float.NaN, float.PositiveInfinity, 0.1001F })
        {
            var result = ConfigValidator.Validate(CreateValidConfiguration(
                safety: new SafetySettings(floatTolerance: tolerance)));
            AssertEx.False(result.IsValid);
            AssertCodes(result, ValidationIssueCode.InvalidFloatTolerance);
        }

        foreach (var window in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromTicks(-1),
                     TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1),
                 })
        {
            var result = ConfigValidator.Validate(CreateValidConfiguration(
                safety: new SafetySettings(echoSuppressionWindow: window)));
            AssertEx.False(result.IsValid);
            AssertCodes(result, ValidationIssueCode.InvalidEchoWindow);
        }

        var boundary = ConfigValidator.Validate(CreateValidConfiguration(
            safety: new SafetySettings(
                floatTolerance: 0.1F,
                echoSuppressionWindow: TimeSpan.FromMinutes(1))));
        AssertEx.True(boundary.IsValid);
    }

    private static void UnsafeVerificationPolicyIsRejected()
    {
        var result = ConfigValidator.Validate(CreateValidConfiguration(
            safety: new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: false)));

        AssertEx.False(result.IsValid);
        var issue = SingleIssue(result, ValidationIssueCode.UnsafeVerificationPolicy);
        AssertEx.Equal(ValidationSeverity.Error, issue.Severity);
        AssertEx.Equal("Safety.StopOnVerificationFailure", issue.Path);

        var noReadback = ConfigValidator.Validate(CreateValidConfiguration(
            safety: new SafetySettings(
                dryRun: false,
                requireReadback: false,
                stopOnVerificationFailure: true)));
        AssertEx.False(noReadback.IsValid);
        var readbackIssue = SingleIssue(
            noReadback,
            ValidationIssueCode.UnsafeVerificationPolicy);
        AssertEx.Equal("Safety.RequireReadback", readbackIssue.Path);
    }

    private static void DiscoveryIdentityIsChecked()
    {
        var configured = new WingEndpoint(
            "2001:db8::10",
            ExpectedSerial: "SERIAL-ABC");
        var matching = new DiscoveredWing(
            "2001:0db8:0:0:0:0:0:10",
            "WING",
            "wing",
            " serial-abc ",
            "3.1",
            DateTimeOffset.UnixEpoch);
        AssertEx.Equal(
            0,
            ConfigValidator.ValidateDiscoveredIdentity(configured, matching, "FOH").Count);

        var mismatching = matching with
        {
            IpAddress = "2001:db8::11",
            SerialNumber = "OTHER",
        };
        var issues = ConfigValidator.ValidateDiscoveredIdentity(
            configured,
            mismatching,
            "FOH");
        AssertEx.SequenceEqual(
        [
            ValidationIssueCode.AddressMismatch,
            ValidationIssueCode.SerialMismatch,
        ],
            issues.Select(static issue => issue.Code));
    }

    private static void HighRiskClassificationIsComplete()
    {
        SyncScope[] highRisk =
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
        SyncScope[] lowerRisk =
        [
            SyncScope.Cust,
            SyncScope.In,
            SyncScope.Filter,
            SyncScope.Delay,
            SyncScope.Gate,
            SyncScope.Dyn,
            SyncScope.Eq,
            SyncScope.Pan,
        ];

        AssertEx.True(highRisk.All(ConfigValidator.IsHighRiskScope));
        AssertEx.True(lowerRisk.All(static scope => !ConfigValidator.IsHighRiskScope(scope)));
        AssertEx.Equal(Enum.GetValues<SyncScope>().Length, highRisk.Length + lowerRisk.Length);
    }

    private static void ValidationOrderIsDeterministic()
    {
        var configuration = new AppConfiguration(
            new WingEndpoint("not-an-ip", 0),
            new WingEndpoint("not-an-ip", 0),
            SyncDirection.Bidirectional,
            (InitialSync)999,
            new SafetySettings(floatTolerance: 0F, echoSuppressionWindow: TimeSpan.Zero),
            [],
            new ChannelMapping());

        var first = ConfigValidator.Validate(configuration).Issues;
        var second = ConfigValidator.Validate(configuration).Issues;
        AssertEx.SequenceEqual(first, second);
    }

    private static AppConfiguration CreateValidConfiguration(
        WingEndpoint? foh = null,
        WingEndpoint? monitor = null,
        SyncDirection direction = SyncDirection.FohToMonitor,
        InitialSync initialSync = InitialSync.PreviewOnly,
        SafetySettings? safety = null,
        IEnumerable<SyncScope>? scopes = null,
        ChannelMapping? channels = null) =>
        new(
            foh ?? new WingEndpoint("10.0.0.10", ExpectedSerial: "FOH-1"),
            monitor ?? new WingEndpoint("10.0.0.11", ExpectedSerial: "MON-1"),
            direction,
            initialSync,
            safety ?? SafetySettings.SafeDefaults,
            scopes ?? [SyncScope.Eq],
            channels ?? new ChannelMapping([new InputChannelMapping(1, 1)]));

    private static SafetySettings LiveSafety(bool allowHighRiskWrites = false) =>
        new(
            dryRun: false,
            requireReadback: true,
            allowHighRiskWrites: allowHighRiskWrites);

    private static void AssertCodes(
        ConfigurationValidationResult result,
        params ValidationIssueCode[] expectedCodes)
    {
        var actual = result.Issues.Select(static issue => issue.Code).ToArray();
        foreach (var code in expectedCodes)
        {
            AssertEx.Contains(actual, code);
        }
    }

    private static ValidationIssue SingleIssue(
        ConfigurationValidationResult result,
        ValidationIssueCode code)
    {
        var matches = result.Issues.Where(issue => issue.Code == code).ToArray();
        AssertEx.Equal(1, matches.Length);
        return matches[0];
    }
}
