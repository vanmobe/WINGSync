using WingSync.Core.Domain;
using WingSync.Core.Planning;

namespace WingSync.Core.Tests;

internal static class PlanningTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    public static void Register(TestSuite suite)
    {
        suite.Add("Comparer.Integer and string comparison is exact", IntegerAndStringComparisonIsExact);
        suite.Add("Comparer.Float comparison handles absolute and relative tolerance", FloatToleranceIsApplied);
        suite.Add("Comparer.Invalid tolerance is rejected", InvalidToleranceIsRejected);
        suite.Add("Echo.Fingerprint is deterministic and typed", FingerprintIsDeterministicAndTyped);
        suite.Add("Echo.Match requires path value and active lifetime", EchoMatchIsBounded);
        suite.Add("Echo.Invalid creation parameters are rejected", InvalidFingerprintParametersAreRejected);
        suite.Add("Planner.Low-risk live write maps and executes", LowRiskLiveWriteMapsAndExecutes);
        suite.Add("Planner.Dry-run produces no executable writes", DryRunProducesNoExecutableWrites);
        suite.Add("Planner.High-risk writes require explicit switch", HighRiskWritesRequireExplicitSwitch);
        suite.Add("Planner.Fail-closed issues retain source changes", FailClosedIssuesAreReported);
        suite.Add("Planner.Last target value wins deterministically", LastTargetValueWins);
        suite.Add("Planner.Model changes are safely phased", ModelChangesAreSafelyPhased);
        suite.Add("Planner.Filter model changes guard LC, HC, and TF", FilterModelChangesGuardCuts);
        suite.Add("Planner.Delay mode and value are one guarded tuple", DelayTupleIsSafelyPhased);
        suite.Add("Planner.Bypass model changes are safely phased", BypassChangesAreSafelyPhased);
        suite.Add("Planner.Disabled model state precedes model change", DisabledStatePrecedesModelChange);
        suite.Add("Planner.Dynamic group order follows first observation", DynamicGroupOrderIsStable);
        suite.Add("Planner.Unresolved gate sidechain blocks its complete group", UnresolvedGateSidechainBlocksCompleteGroup);
        suite.Add("Planner.Unresolved dynamics sidechain blocks its complete group", UnresolvedDynamicsSidechainBlocksCompleteGroup);
        suite.Add("Planner.Mapped channel sidechains keep complete groups executable", MappedChannelSidechainsKeepCompleteGroupsExecutable);
        suite.Add("Planner.Sidechain values are mapped with target token", PlannerMapsSidechainValue);
        suite.Add("Planner.Readback policy flows into every write", ReadbackPolicyFlowsIntoWrites);
        suite.Add("Planner.Risk classifier covers public scopes", RiskClassifierCoversScopes);
        suite.Add("Planner.Argument contracts fail fast", PlannerArgumentsFailFast);
        suite.Add("Planner.Write plan collections are defensive", WritePlanCollectionsAreDefensive);
        suite.Add("Planner.Coalescing follows rewritten target", CoalescingFollowsRewrittenTarget);
    }

    private static void IntegerAndStringComparisonIsExact()
    {
        AssertEx.True(WingValueComparer.AreEquivalent(
            WingValue.FromInt32(7),
            WingValue.FromInt32(7)));
        AssertEx.False(WingValueComparer.AreEquivalent(
            WingValue.FromInt32(7),
            WingValue.FromInt32(8)));
        AssertEx.True(WingValueComparer.AreEquivalent(
            WingValue.FromString("ON"),
            WingValue.FromString("ON")));
        AssertEx.False(WingValueComparer.AreEquivalent(
            WingValue.FromString("ON"),
            WingValue.FromString("on")));
        AssertEx.False(WingValueComparer.AreEquivalent(
            WingValue.FromInt32(1),
            WingValue.FromFloat(1F)));
    }

    private static void FloatToleranceIsApplied()
    {
        const float tolerance = 0.001F;
        AssertEx.True(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(0F),
            WingValue.FromFloat(0.0009F),
            tolerance));
        AssertEx.False(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(0F),
            WingValue.FromFloat(0.0011F),
            tolerance));
        AssertEx.True(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(1000F),
            WingValue.FromFloat(1000.9F),
            tolerance));
        AssertEx.False(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(1000F),
            WingValue.FromFloat(1001.1F),
            tolerance));
        AssertEx.True(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(-0F),
            WingValue.FromFloat(0F),
            tolerance));
        AssertEx.True(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(float.PositiveInfinity),
            WingValue.FromFloat(float.PositiveInfinity),
            tolerance));
        AssertEx.False(WingValueComparer.AreEquivalent(
            WingValue.FromFloat(float.PositiveInfinity),
            WingValue.FromFloat(float.NegativeInfinity),
            tolerance));
    }

    private static void InvalidToleranceIsRejected()
    {
        foreach (var tolerance in new[] { 0F, -1F, float.NaN, float.PositiveInfinity })
        {
            AssertEx.Throws<ArgumentOutOfRangeException>(
                () => WingValueComparer.AreEquivalent(
                    WingValue.FromFloat(1F),
                    WingValue.FromFloat(1F),
                    tolerance));
        }
    }

    private static void FingerprintIsDeterministicAndTyped()
    {
        var path = TokenPath.Parse("/ch/9/eq/on");
        var first = EchoFingerprint.Create(
            path,
            WingValue.FromInt32(1),
            FixedTime,
            TimeSpan.FromSeconds(2),
            0.0001F);
        var second = EchoFingerprint.Create(
            path,
            WingValue.FromInt32(1),
            FixedTime.AddMinutes(1),
            TimeSpan.FromSeconds(5),
            0.0001F);
        var typedDifferently = EchoFingerprint.Create(
            path,
            WingValue.FromFloat(1F),
            FixedTime,
            TimeSpan.FromSeconds(2),
            0.0001F);

        AssertEx.Equal(first.Key, second.Key);
        AssertEx.NotEqual(first.Key, typedDifferently.Key);
        AssertEx.Equal(64, first.Key.Length);
        AssertEx.Equal(FixedTime + TimeSpan.FromSeconds(2), first.ExpiresAt);
    }

    private static void EchoMatchIsBounded()
    {
        var path = TokenPath.Parse("/ch/9/eq/1/g");
        var fingerprint = EchoFingerprint.Create(
            path,
            WingValue.FromFloat(2F),
            FixedTime,
            TimeSpan.FromSeconds(2),
            0.001F);

        AssertEx.True(fingerprint.Matches(
            path,
            WingValue.FromFloat(2.0009F),
            FixedTime,
            0.001F));
        AssertEx.True(fingerprint.Matches(
            path,
            WingValue.FromFloat(2F),
            FixedTime.AddSeconds(2),
            0.001F));
        AssertEx.False(fingerprint.Matches(
            path,
            WingValue.FromFloat(2F),
            FixedTime.AddTicks(-1),
            0.001F));
        AssertEx.False(fingerprint.Matches(
            path,
            WingValue.FromFloat(2F),
            FixedTime.AddSeconds(2).AddTicks(1),
            0.001F));
        AssertEx.False(fingerprint.Matches(
            TokenPath.Parse("/ch/10/eq/1/g"),
            WingValue.FromFloat(2F),
            FixedTime,
            0.001F));
        AssertEx.False(fingerprint.Matches(
            path,
            WingValue.FromFloat(2.01F),
            FixedTime,
            0.001F));
    }

    private static void InvalidFingerprintParametersAreRejected()
    {
        var path = TokenPath.Parse("/ch/1/eq/on");
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => EchoFingerprint.Create(
                path,
                WingValue.FromInt32(1),
                FixedTime,
                TimeSpan.Zero,
                0.0001F));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => EchoFingerprint.Create(
                path,
                WingValue.FromInt32(1),
                FixedTime,
                TimeSpan.FromSeconds(1),
                0F));
        AssertEx.Throws<ArgumentNullException>(
            () => EchoFingerprint.Create(
                null!,
                WingValue.FromInt32(1),
                FixedTime,
                TimeSpan.FromSeconds(1),
                0.0001F));
    }

    private static void LowRiskLiveWriteMapsAndExecutes()
    {
        var plan = Plan(
            [Change("/ch/1/eq/1/g", WingValue.FromFloat(4.5F))],
            scopes: [SyncScope.Eq],
            safety: LiveSafety(),
            mapping: new ChannelMapping([new InputChannelMapping(1, 9)]));

        AssertEx.Equal(1, plan.Writes.Count);
        AssertEx.Equal(1, plan.ExecutableWrites.Count);
        AssertEx.Equal(0, plan.Issues.Count);
        var write = plan.Writes[0];
        AssertEx.Equal(0, write.Sequence);
        AssertEx.Equal("/ch/1/eq/1/g", write.SourcePath.ToString());
        AssertEx.Equal("/ch/9/eq/1/g", write.TargetPath.ToString());
        AssertEx.Equal(4.5F, write.Value.AsFloat());
        AssertEx.Equal(SyncScope.Eq, write.Scope);
        AssertEx.Equal(WriteRisk.AudibleProcessing, write.Risk);
        AssertEx.Equal(WriteDisposition.Execute, write.Disposition);
        AssertEx.True(write.RequiresReadback);
        AssertEx.False(write.IsSafetyGuard);
        AssertEx.Equal(FixedTime, write.Echo.CreatedAt);
    }

    private static void DryRunProducesNoExecutableWrites()
    {
        var plan = Plan(
            [Change("/ch/1/name", WingValue.FromString("Lead"))],
            scopes: [SyncScope.Cust],
            safety: SafetySettings.SafeDefaults);

        AssertEx.Equal(1, plan.Writes.Count);
        AssertEx.Equal(0, plan.ExecutableWrites.Count);
        AssertEx.Equal(WriteDisposition.DryRun, plan.Writes[0].Disposition);
        AssertEx.Equal(WriteRisk.Cosmetic, plan.Writes[0].Risk);
    }

    private static void HighRiskWritesRequireExplicitSwitch()
    {
        var safePreview = Plan(
            [Change("/ch/1/mute", WingValue.FromInt32(1))],
            scopes: [SyncScope.Mute],
            safety: SafetySettings.SafeDefaults);
        AssertEx.Equal(WriteDisposition.DryRun, safePreview.Writes[0].Disposition);
        AssertEx.Equal(0, safePreview.ExecutableWrites.Count);

        var blocked = Plan(
            [Change("/ch/1/mute", WingValue.FromInt32(1))],
            scopes: [SyncScope.Mute],
            safety: LiveSafety());
        AssertEx.Equal(WriteDisposition.BlockedBySafety, blocked.Writes[0].Disposition);
        AssertEx.Equal(WriteRisk.MixControl, blocked.Writes[0].Risk);
        AssertEx.Equal(0, blocked.ExecutableWrites.Count);

        var allowedDryRun = Plan(
            [Change("/ch/1/mute", WingValue.FromInt32(1))],
            scopes: [SyncScope.Mute],
            safety: new SafetySettings(allowHighRiskWrites: true));
        AssertEx.Equal(WriteDisposition.DryRun, allowedDryRun.Writes[0].Disposition);

        var allowedLive = Plan(
            [Change("/ch/1/mute", WingValue.FromInt32(1))],
            scopes: [SyncScope.Mute],
            safety: LiveSafety(allowHighRiskWrites: true));
        AssertEx.Equal(WriteDisposition.Execute, allowedLive.Writes[0].Disposition);
        AssertEx.Equal(1, allowedLive.ExecutableWrites.Count);
    }

    private static void FailClosedIssuesAreReported()
    {
        var mapping = new ChannelMapping([new InputChannelMapping(1, 9)]);
        var plan = Plan(
        [
            Change("/ch/1/$stat", WingValue.FromString("x")),
            Change("/io/in/1/gain", WingValue.FromFloat(12F)),
            Change("/ch/1/eq/on", WingValue.FromInt32(1)),
            Change("/ch/2/eq/on", WingValue.FromInt32(1)),
            Change("/ch/1/gatesc/src", WingValue.FromString("CH.2")),
        ],
            scopes: [SyncScope.Gate],
            safety: LiveSafety(),
            mapping: mapping);

        AssertEx.Equal(0, plan.Writes.Count);
        AssertEx.SequenceEqual(
        [
            PlanningIssueCode.ReadOnlyToken,
            PlanningIssueCode.UnsupportedToken,
            PlanningIssueCode.DisabledScope,
            PlanningIssueCode.MissingChannelMapping,
            PlanningIssueCode.UnresolvedSidechainReference,
        ],
            plan.Issues.Select(static issue => issue.Code));
    }

    private static void LastTargetValueWins()
    {
        var plan = Plan(
        [
            Change("/ch/1/eq/1/g", WingValue.FromFloat(1F), revision: 1),
            Change("/ch/1/eq/1/g", WingValue.FromFloat(2F), revision: 2),
            Change("/ch/1/eq/1/g", WingValue.FromFloat(3F), revision: 3),
        ],
            scopes: [SyncScope.Eq],
            safety: LiveSafety());

        AssertEx.Equal(1, plan.Writes.Count);
        AssertEx.Equal(2, plan.SupersededChangeCount);
        AssertEx.Equal(3F, plan.Writes[0].Value.AsFloat());
    }

    private static void ModelChangesAreSafelyPhased()
    {
        var plan = Plan(
        [
            Change("/ch/1/gate/thr", WingValue.FromFloat(-18F)),
            Change("/ch/1/gate/on", WingValue.FromInt32(1)),
            Change("/ch/1/gate/mdl", WingValue.FromString("EXP2")),
        ],
            scopes: [SyncScope.Gate],
            safety: LiveSafety());

        AssertEx.SequenceEqual(
        [
            "/ch/1/gate/on",
            "/ch/1/gate/mdl",
            "/ch/1/gate/thr",
            "/ch/1/gate/on",
        ],
            plan.Writes.Select(static write => write.TargetPath.ToString()));
        AssertEx.SequenceEqual(
            [0, 1, 2, 3],
            plan.Writes.Select(static write => write.Sequence));
        AssertEx.True(plan.Writes[0].IsSafetyGuard);
        AssertEx.Equal(0, plan.Writes[0].Value.AsInt32());
        AssertEx.False(plan.Writes[1].IsSafetyGuard);
        AssertEx.Equal("EXP2", plan.Writes[1].Value.AsString());
        AssertEx.Equal(-18F, plan.Writes[2].Value.AsFloat());
        AssertEx.Equal(1, plan.Writes[3].Value.AsInt32());
    }

    private static void FilterModelChangesGuardCuts()
    {
        var plan = Plan(
        [
            Change("/ch/1/flt/lc", WingValue.FromInt32(1)),
            Change("/ch/1/flt/hc", WingValue.FromInt32(1)),
            Change("/ch/1/flt/tf", WingValue.FromInt32(1)),
            Change("/ch/1/flt/mdl", WingValue.FromString("TILT")),
            Change("/ch/1/flt/1", WingValue.FromFloat(120F)),
        ],
            scopes: [SyncScope.Filter],
            safety: LiveSafety());

        AssertEx.SequenceEqual(
        [
            "/ch/1/flt/lc",
            "/ch/1/flt/hc",
            "/ch/1/flt/tf",
            "/ch/1/flt/mdl",
            "/ch/1/flt/1",
            "/ch/1/flt/lc",
            "/ch/1/flt/hc",
            "/ch/1/flt/tf",
        ],
            plan.Writes.Select(static write => write.TargetPath.ToString()));
        AssertEx.True(plan.Writes[0].IsSafetyGuard);
        AssertEx.True(plan.Writes[1].IsSafetyGuard);
        AssertEx.True(plan.Writes[2].IsSafetyGuard);
        AssertEx.Equal(0, plan.Writes[0].Value.AsInt32());
        AssertEx.Equal(0, plan.Writes[1].Value.AsInt32());
        AssertEx.Equal(0, plan.Writes[2].Value.AsInt32());
        AssertEx.Equal(1, plan.Writes[5].Value.AsInt32());
        AssertEx.Equal(1, plan.Writes[6].Value.AsInt32());
        AssertEx.Equal(1, plan.Writes[7].Value.AsInt32());
    }

    private static void DelayTupleIsSafelyPhased()
    {
        var plan = Plan(
        [
            Change("/ch/1/in/set/dlymode", WingValue.FromString("M")),
            Change("/ch/1/in/set/dly", WingValue.FromFloat(1F)),
            Change("/ch/1/in/set/dlyon", WingValue.FromInt32(1)),
        ],
            scopes: [SyncScope.Delay],
            safety: LiveSafety());

        AssertEx.SequenceEqual(
        [
            "/ch/1/in/set/dlyon",
            "/ch/1/in/set/dlymode",
            "/ch/1/in/set/dly",
            "/ch/1/in/set/dlyon",
        ],
            plan.Writes.Select(static write => write.TargetPath.ToString()));
        AssertEx.True(plan.Writes[0].IsSafetyGuard);
        AssertEx.Equal(0, plan.Writes[0].Value.AsInt32());
        AssertEx.Equal("M", plan.Writes[1].Value.AsString());
        AssertEx.Equal(1F, plan.Writes[2].Value.AsFloat());
        AssertEx.Equal(1, plan.Writes[3].Value.AsInt32());
    }

    private static void BypassChangesAreSafelyPhased()
    {
        var plan = Plan(
        [
            Change("/ch/1/dyn/byp", WingValue.FromString("OFF")),
            Change("/ch/1/dyn/mdl", WingValue.FromString("COMP")),
            Change("/ch/1/dyn/ratio", WingValue.FromFloat(4F)),
        ],
            scopes: [SyncScope.Dyn],
            safety: LiveSafety());

        AssertEx.Equal(4, plan.Writes.Count);
        AssertEx.True(plan.Writes[0].IsSafetyGuard);
        AssertEx.Equal("ON", plan.Writes[0].Value.AsString());
        AssertEx.Equal("/ch/1/dyn/mdl", plan.Writes[1].TargetPath.ToString());
        AssertEx.Equal("/ch/1/dyn/ratio", plan.Writes[2].TargetPath.ToString());
        AssertEx.Equal("OFF", plan.Writes[3].Value.AsString());
    }

    private static void DisabledStatePrecedesModelChange()
    {
        var plan = Plan(
        [
            Change("/ch/1/eq/mdl", WingValue.FromString("PEQ")),
            Change("/ch/1/eq/on", WingValue.FromInt32(0)),
        ],
            scopes: [SyncScope.Eq],
            safety: LiveSafety());

        AssertEx.Equal(2, plan.Writes.Count);
        AssertEx.False(plan.Writes.Any(static write => write.IsSafetyGuard));
        AssertEx.Equal("/ch/1/eq/on", plan.Writes[0].TargetPath.ToString());
        AssertEx.Equal("/ch/1/eq/mdl", plan.Writes[1].TargetPath.ToString());
    }

    private static void DynamicGroupOrderIsStable()
    {
        var plan = Plan(
        [
            Change("/ch/2/eq/mdl", WingValue.FromString("PEQ")),
            Change("/ch/1/gate/mdl", WingValue.FromString("GATE")),
            Change("/ch/2/eq/1/g", WingValue.FromFloat(1F)),
            Change("/ch/1/gate/thr", WingValue.FromFloat(-12F)),
        ],
            scopes: [SyncScope.Eq, SyncScope.Gate],
            safety: LiveSafety(),
            mapping: new ChannelMapping(
            [
                new InputChannelMapping(1, 1),
                new InputChannelMapping(2, 2),
            ]));

        AssertEx.SequenceEqual(
        [
            "/ch/2/eq/mdl",
            "/ch/2/eq/1/g",
            "/ch/1/gate/mdl",
            "/ch/1/gate/thr",
        ],
            plan.Writes.Select(static write => write.TargetPath.ToString()));
    }

    private static void UnresolvedGateSidechainBlocksCompleteGroup()
    {
        var plan = Plan(
        [
            Change("/ch/1/gate/thr", WingValue.FromFloat(-18F)),
            Change("/ch/1/gate/hold", WingValue.FromFloat(250F)),
            Change("/ch/1/gate/on", WingValue.FromInt32(1)),
            Change("/ch/1/gatesc/src", WingValue.FromString("CH.2")),
        ],
            scopes: [SyncScope.Gate],
            safety: LiveSafety(),
            mapping: new ChannelMapping([new InputChannelMapping(1, 9)]));

        AssertEx.Equal(0, plan.Writes.Count);
        AssertEx.Equal(0, plan.ExecutableWrites.Count);
        AssertEx.Equal(1, plan.Issues.Count);
        AssertEx.Equal(
            PlanningIssueCode.UnresolvedSidechainReference,
            plan.Issues[0].Code);
        AssertEx.Equal("/ch/1/gatesc/src", plan.Issues[0].SourcePath.ToString());
    }

    private static void UnresolvedDynamicsSidechainBlocksCompleteGroup()
    {
        var plan = Plan(
        [
            Change("/ch/1/dyn/ratio", WingValue.FromFloat(4F)),
            Change("/ch/1/dyn/attack", WingValue.FromFloat(12F)),
            Change("/ch/1/dyn/byp", WingValue.FromString("OFF")),
            Change("/ch/1/dynxo/f1", WingValue.FromFloat(120F)),
            Change("/ch/1/dynsc/src", WingValue.FromString("CH.2")),
        ],
            scopes: [SyncScope.Dyn],
            safety: LiveSafety(),
            mapping: new ChannelMapping([new InputChannelMapping(1, 9)]));

        AssertEx.Equal(0, plan.Writes.Count);
        AssertEx.Equal(0, plan.ExecutableWrites.Count);
        AssertEx.Equal(1, plan.Issues.Count);
        AssertEx.Equal(
            PlanningIssueCode.UnresolvedSidechainReference,
            plan.Issues[0].Code);
        AssertEx.Equal("/ch/1/dynsc/src", plan.Issues[0].SourcePath.ToString());
    }

    private static void MappedChannelSidechainsKeepCompleteGroupsExecutable()
    {
        var mapping = new ChannelMapping(
        [
            new InputChannelMapping(1, 9),
            new InputChannelMapping(2, 12),
        ]);
        var gate = Plan(
        [
            Change("/ch/1/gate/thr", WingValue.FromFloat(-18F)),
            Change("/ch/1/gate/on", WingValue.FromInt32(1)),
            Change("/ch/1/gatesc/src", WingValue.FromString("CH.2")),
        ],
            scopes: [SyncScope.Gate],
            safety: LiveSafety(),
            mapping: mapping);
        var dynamics = Plan(
        [
            Change("/ch/1/dyn/ratio", WingValue.FromFloat(4F)),
            Change("/ch/1/dyn/byp", WingValue.FromString("OFF")),
            Change("/ch/1/dynxo/f1", WingValue.FromFloat(120F)),
            Change("/ch/1/dynsc/src", WingValue.FromString("CH.2")),
        ],
            scopes: [SyncScope.Dyn],
            safety: LiveSafety(),
            mapping: mapping);

        AssertEx.Equal(0, gate.Issues.Count);
        AssertEx.Equal(3, gate.Writes.Count);
        AssertEx.Equal(3, gate.ExecutableWrites.Count);
        AssertEx.True(gate.Writes.All(static write =>
            write.Disposition == WriteDisposition.Execute));
        AssertEx.SequenceEqual(
        [
            "/ch/9/gate/on",
            "/ch/9/gate/thr",
            "/ch/9/gatesc/src",
        ],
            gate.Writes
                .Select(static write => write.TargetPath.ToString())
                .OrderBy(static path => path, StringComparer.Ordinal));
        AssertEx.Equal(
            "CH.12",
            gate.Writes.Single(static write =>
                write.TargetPath.ToString() == "/ch/9/gatesc/src").Value.AsString());

        AssertEx.Equal(0, dynamics.Issues.Count);
        AssertEx.Equal(4, dynamics.Writes.Count);
        AssertEx.Equal(4, dynamics.ExecutableWrites.Count);
        AssertEx.True(dynamics.Writes.All(static write =>
            write.Disposition == WriteDisposition.Execute));
        AssertEx.SequenceEqual(
        [
            "/ch/9/dyn/byp",
            "/ch/9/dyn/ratio",
            "/ch/9/dynsc/src",
            "/ch/9/dynxo/f1",
        ],
            dynamics.Writes
                .Select(static write => write.TargetPath.ToString())
                .OrderBy(static path => path, StringComparer.Ordinal));
        AssertEx.Equal(
            "CH.12",
            dynamics.Writes.Single(static write =>
                write.TargetPath.ToString() == "/ch/9/dynsc/src").Value.AsString());
    }

    private static void PlannerMapsSidechainValue()
    {
        var mapping = new ChannelMapping(
        [
            new InputChannelMapping(1, 9),
            new InputChannelMapping(2, 12),
        ]);
        var plan = Plan(
            [Change("/ch/1/gatesc/src", WingValue.FromString("CH.2"))],
            scopes: [SyncScope.Gate],
            safety: LiveSafety(),
            mapping: mapping);

        AssertEx.Equal("/ch/9/gatesc/src", plan.Writes[0].TargetPath.ToString());
        AssertEx.Equal("CH.12", plan.Writes[0].Value.AsString());
    }

    private static void ReadbackPolicyFlowsIntoWrites()
    {
        var safety = new SafetySettings(
            dryRun: false,
            requireReadback: false,
            allowHighRiskWrites: false);
        var plan = Plan(
        [
            Change("/ch/1/eq/mdl", WingValue.FromString("PEQ")),
            Change("/ch/1/eq/on", WingValue.FromInt32(1)),
        ],
            scopes: [SyncScope.Eq],
            safety: safety);

        AssertEx.True(plan.Writes.All(static write => !write.RequiresReadback));
    }

    private static void RiskClassifierCoversScopes()
    {
        var path = TokenPath.Parse("/ch/1/eq/on");
        AssertEx.Equal(WriteRisk.Cosmetic, WriteRiskClassifier.Classify(SyncScope.Cust, path));
        AssertEx.Equal(WriteRisk.AudibleProcessing, WriteRiskClassifier.Classify(SyncScope.Eq, path));
        AssertEx.Equal(WriteRisk.MixControl, WriteRiskClassifier.Classify(SyncScope.Fdr, path));
        AssertEx.Equal(WriteRisk.Routing, WriteRiskClassifier.Classify(SyncScope.Conn, path));
        AssertEx.Equal(WriteRisk.Critical, WriteRiskClassifier.Classify((SyncScope)999, path));
        AssertEx.Equal(
            WriteRisk.Critical,
            WriteRiskClassifier.Classify(SyncScope.Eq, TokenPath.Parse("/io/in/1/gain")));
    }

    private static void PlannerArgumentsFailFast()
    {
        var planner = new WritePlanner();
        var mapping = new ChannelMapping([new InputChannelMapping(1, 1)]);
        var safety = SafetySettings.SafeDefaults;

        AssertEx.Throws<ArgumentNullException>(
            () => planner.Plan(null!, mapping, [SyncScope.Eq], safety));
        AssertEx.Throws<ArgumentNullException>(
            () => planner.Plan([], null!, [SyncScope.Eq], safety));
        AssertEx.Throws<ArgumentNullException>(
            () => planner.Plan([], mapping, null!, safety));
        AssertEx.Throws<ArgumentNullException>(
            () => planner.Plan([], mapping, [SyncScope.Eq], null!));
        AssertEx.Throws<ArgumentNullException>(
            () => planner.Plan(
                [null!],
                mapping,
                [SyncScope.Eq],
                safety));
    }

    private static void WritePlanCollectionsAreDefensive()
    {
        var writes = new List<PlannedWrite>();
        var issues = new List<PlanningIssue>();
        var plan = new WritePlan(writes, issues, 0);
        writes.Add(CreateSyntheticWrite());
        issues.Add(new PlanningIssue(
            PlanningIssueCode.UnsupportedToken,
            TokenPath.Parse("/ch/1/eq/on"),
            "test"));

        AssertEx.Equal(0, plan.Writes.Count);
        AssertEx.Equal(0, plan.ExecutableWrites.Count);
        AssertEx.Equal(0, plan.Issues.Count);
    }

    private static void CoalescingFollowsRewrittenTarget()
    {
        var mapping = new ChannelMapping(
        [
            new InputChannelMapping(1, 9),
            new InputChannelMapping(2, 9),
        ]);
        var plan = Plan(
        [
            Change("/ch/1/eq/on", WingValue.FromInt32(1), revision: 1),
            Change("/ch/2/eq/on", WingValue.FromInt32(0), revision: 2),
        ],
            scopes: [SyncScope.Eq],
            safety: LiveSafety(),
            mapping: mapping);

        AssertEx.Equal(1, plan.Writes.Count);
        AssertEx.Equal(1, plan.SupersededChangeCount);
        AssertEx.Equal("/ch/2/eq/on", plan.Writes[0].SourcePath.ToString());
        AssertEx.Equal("/ch/9/eq/on", plan.Writes[0].TargetPath.ToString());
        AssertEx.Equal(0, plan.Writes[0].Value.AsInt32());
    }

    private static PlannedWrite CreateSyntheticWrite()
    {
        var path = TokenPath.Parse("/ch/1/eq/on");
        return new PlannedWrite(
            0,
            path,
            path,
            WingValue.FromInt32(1),
            SyncScope.Eq,
            WriteRisk.AudibleProcessing,
            WriteDisposition.Execute,
            true,
            false,
            EchoFingerprint.Create(
                path,
                WingValue.FromInt32(1),
                FixedTime,
                TimeSpan.FromSeconds(1),
                0.0001F));
    }

    private static WritePlan Plan(
        IEnumerable<SyncChange> changes,
        IEnumerable<SyncScope> scopes,
        SafetySettings safety,
        ChannelMapping? mapping = null) =>
        new WritePlanner().Plan(
            changes,
            mapping ?? new ChannelMapping([new InputChannelMapping(1, 1)]),
            scopes,
            safety,
            FixedTime);

    private static SyncChange Change(
        string path,
        WingValue value,
        long revision = 1) =>
        new(TokenPath.Parse(path), value, FixedTime, revision);

    private static SafetySettings LiveSafety(bool allowHighRiskWrites = false) =>
        new(
            dryRun: false,
            requireReadback: true,
            allowHighRiskWrites: allowHighRiskWrites);
}
