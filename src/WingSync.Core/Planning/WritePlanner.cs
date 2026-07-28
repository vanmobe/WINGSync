using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WingSync.Core.Domain;

namespace WingSync.Core.Planning;

/// <summary>
/// Represents one freshly observed source-console change offered to the planner.
/// </summary>
/// <param name="SourcePath">The canonical source token.</param>
/// <param name="Value">The typed source value.</param>
/// <param name="ObservedAt">When the event or snapshot value was observed.</param>
/// <param name="SourceRevision">The locally monotonic source-cache revision.</param>
public sealed record SyncChange(
    TokenPath SourcePath,
    WingValue Value,
    DateTimeOffset ObservedAt,
    long SourceRevision);

/// <summary>
/// Classifies the potential show impact of a write.
/// </summary>
public enum WriteRisk
{
    /// <summary>Cosmetic metadata without an expected audio consequence.</summary>
    Cosmetic,

    /// <summary>A processing value that can alter the sound of a mapped channel.</summary>
    AudibleProcessing,

    /// <summary>A fader, mute, main, or send control that can immediately alter the mix.</summary>
    MixControl,

    /// <summary>A routing, assignment, DCA, insert, or process-configuration change.</summary>
    Routing,

    /// <summary>An unsupported or physical-I/O operation that must remain blocked.</summary>
    Critical,
}

/// <summary>
/// Indicates whether a planned write can be sent by the execution layer.
/// </summary>
public enum WriteDisposition
{
    /// <summary>The write passed scope and safety policy and may be executed.</summary>
    Execute,

    /// <summary>The write is valid but remains a preview because dry-run mode is active.</summary>
    DryRun,

    /// <summary>The write is valid but explicitly blocked by the high-risk safety switch.</summary>
    BlockedBySafety,
}

/// <summary>
/// Stable machine-readable reasons for skipping a source change during planning.
/// </summary>
public enum PlanningIssueCode
{
    /// <summary>A read-only token was offered.</summary>
    ReadOnlyToken,

    /// <summary>The token does not belong to the supported scope catalog.</summary>
    UnsupportedToken,

    /// <summary>The token's scope is not enabled.</summary>
    DisabledScope,

    /// <summary>The token's own source channel has no unique mapping.</summary>
    MissingChannelMapping,

    /// <summary>An embedded sidechain channel reference has no unique mapping.</summary>
    UnresolvedSidechainReference,
}

/// <summary>
/// Describes a source change that was intentionally excluded from the write plan.
/// </summary>
/// <param name="Code">The stable skip reason.</param>
/// <param name="SourcePath">The affected source token.</param>
/// <param name="Message">An actionable operator-facing explanation.</param>
public sealed record PlanningIssue(
    PlanningIssueCode Code,
    TokenPath SourcePath,
    string Message);

/// <summary>
/// Identifies a recently emitted target value for feedback-loop suppression.
/// </summary>
/// <param name="Key">A stable SHA-256 key over the canonical path, type, and tolerance bucket.</param>
/// <param name="Path">The emitted target path.</param>
/// <param name="Value">The emitted target value.</param>
/// <param name="CreatedAt">When the outgoing write was planned.</param>
/// <param name="ExpiresAt">When this fingerprint must no longer suppress an event.</param>
public sealed record EchoFingerprint(
    string Key,
    TokenPath Path,
    WingValue Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Creates a bounded fingerprint for an outgoing target value.</summary>
    /// <param name="path">The target token.</param>
    /// <param name="value">The target value.</param>
    /// <param name="createdAt">The local creation time.</param>
    /// <param name="lifetime">The strictly positive suppression duration.</param>
    /// <param name="floatTolerance">The positive float comparison tolerance.</param>
    /// <returns>A deterministic value/path fingerprint with a bounded lifetime.</returns>
    public static EchoFingerprint Create(
        TokenPath path,
        WingValue value,
        DateTimeOffset createdAt,
        TimeSpan lifetime,
        float floatTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ValidateTolerance(floatTolerance);

        var canonicalValue = Canonicalize(value, floatTolerance);
        var canonical = string.Concat(
            path.ToString(),
            "|",
            value.Type.ToString(),
            "|",
            canonicalValue);
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new EchoFingerprint(key, path, value, createdAt, createdAt + lifetime);
    }

    /// <summary>Checks whether an observed target event is this still-active local echo.</summary>
    /// <param name="path">The observed target token.</param>
    /// <param name="value">The observed target value.</param>
    /// <param name="observedAt">When the target event arrived.</param>
    /// <param name="floatTolerance">The configured WING quantization tolerance.</param>
    /// <returns><see langword="true"/> when path, value, and lifetime match.</returns>
    public bool Matches(
        TokenPath path,
        WingValue value,
        DateTimeOffset observedAt,
        float floatTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        return observedAt >= CreatedAt
            && observedAt <= ExpiresAt
            && Path.Equals(path)
            && WingValueComparer.AreEquivalent(Value, value, floatTolerance);
    }

    private static string Canonicalize(WingValue value, float tolerance)
    {
        if (value.Type != WingValueType.F)
        {
            return value.ToString();
        }

        var floatingPointValue = value.AsFloat();
        if (!float.IsFinite(floatingPointValue))
        {
            return floatingPointValue.ToString("R", CultureInfo.InvariantCulture);
        }

        var bucket = Math.Round(
            floatingPointValue / tolerance,
            MidpointRounding.AwayFromZero);
        return bucket.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void ValidateTolerance(float tolerance)
    {
        if (!float.IsFinite(tolerance) || tolerance <= 0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                tolerance,
                "Float tolerance must be finite and greater than zero.");
        }
    }
}

/// <summary>
/// Represents one target write after mapping, safety classification, deduplication, and ordering.
/// </summary>
/// <param name="Sequence">The zero-based execution sequence.</param>
/// <param name="SourcePath">The source token that caused the write.</param>
/// <param name="TargetPath">The mapped target token.</param>
/// <param name="Value">The mapped target value.</param>
/// <param name="Scope">The official WING scope.</param>
/// <param name="Risk">The potential show impact.</param>
/// <param name="Disposition">Whether the execution layer may send the write.</param>
/// <param name="RequiresReadback">Whether target observation is required for success.</param>
/// <param name="IsSafetyGuard">Whether this temporary off/bypass value protects a model change.</param>
/// <param name="Echo">The bounded feedback-suppression fingerprint.</param>
public sealed record PlannedWrite(
    int Sequence,
    TokenPath SourcePath,
    TokenPath TargetPath,
    WingValue Value,
    SyncScope Scope,
    WriteRisk Risk,
    WriteDisposition Disposition,
    bool RequiresReadback,
    bool IsSafetyGuard,
    EchoFingerprint Echo);

/// <summary>
/// Contains an immutable write plan and all fail-closed source-change findings.
/// </summary>
public sealed class WritePlan
{
    private readonly ReadOnlyCollection<PlannedWrite> writes;
    private readonly ReadOnlyCollection<PlannedWrite> executableWrites;
    private readonly ReadOnlyCollection<PlanningIssue> issues;

    /// <summary>Initializes a write plan and defensively copies all collections.</summary>
    /// <param name="writes">Ordered target writes, including preview and blocked items.</param>
    /// <param name="issues">Source changes skipped before planning.</param>
    /// <param name="supersededChangeCount">Number of earlier same-target values removed by deduplication.</param>
    public WritePlan(
        IEnumerable<PlannedWrite> writes,
        IEnumerable<PlanningIssue> issues,
        int supersededChangeCount)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(issues);
        var writeArray = writes.ToArray();
        this.writes = Array.AsReadOnly(writeArray);
        executableWrites = Array.AsReadOnly(
            writeArray
                .Where(static write => write.Disposition == WriteDisposition.Execute)
                .ToArray());
        this.issues = Array.AsReadOnly(issues.ToArray());
        SupersededChangeCount = supersededChangeCount;
    }

    /// <summary>Gets every ordered item, including dry-run and safety-blocked writes.</summary>
    public IReadOnlyList<PlannedWrite> Writes => writes;

    /// <summary>Gets only writes that the execution layer may send.</summary>
    public IReadOnlyList<PlannedWrite> ExecutableWrites => executableWrites;

    /// <summary>Gets source changes skipped because they were unsafe, unmapped, or disabled.</summary>
    public IReadOnlyList<PlanningIssue> Issues => issues;

    /// <summary>Gets the number of same-target values coalesced using last-value-wins semantics.</summary>
    public int SupersededChangeCount { get; }
}

/// <summary>
/// Compares typed write values with explicit tolerance for console-quantized floats.
/// </summary>
public static class WingValueComparer
{
    /// <summary>Compares an expected write with a target readback.</summary>
    /// <param name="expected">The value sent to the target.</param>
    /// <param name="actual">The value subsequently observed from the target.</param>
    /// <param name="floatTolerance">Positive absolute and relative tolerance for floats.</param>
    /// <returns><see langword="true"/> when type and payload are equivalent.</returns>
    public static bool AreEquivalent(
        WingValue expected,
        WingValue actual,
        float floatTolerance = 0.0001F)
    {
        if (!float.IsFinite(floatTolerance) || floatTolerance <= 0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(floatTolerance),
                floatTolerance,
                "Float tolerance must be finite and greater than zero.");
        }

        if (expected.Type != actual.Type)
        {
            return false;
        }

        return expected.Type switch
        {
            WingValueType.I => expected.AsInt32() == actual.AsInt32(),
            WingValueType.S => string.Equals(
                expected.AsString(),
                actual.AsString(),
                StringComparison.Ordinal),
            WingValueType.F => FloatsEquivalent(
                expected.AsFloat(),
                actual.AsFloat(),
                floatTolerance),
            _ => false,
        };
    }

    private static bool FloatsEquivalent(float expected, float actual, float tolerance)
    {
        if (expected.Equals(actual))
        {
            return true;
        }

        if (!float.IsFinite(expected) || !float.IsFinite(actual))
        {
            return false;
        }

        // WAPI may quantize the same control differently at small and large
        // magnitudes, so accept either an absolute or a scale-relative match.
        var difference = MathF.Abs(expected - actual);
        var relativeScale = MathF.Max(MathF.Abs(expected), MathF.Abs(actual));
        return difference <= tolerance
            || difference <= relativeScale * tolerance;
    }
}

/// <summary>
/// Assigns a conservative show-impact category to every catalogued scope.
/// </summary>
public static class WriteRiskClassifier
{
    /// <summary>Classifies one catalogued target write.</summary>
    /// <param name="scope">The matched official WING scope.</param>
    /// <param name="path">The mapped target token.</param>
    /// <returns>The conservative risk category.</returns>
    public static WriteRisk Classify(SyncScope scope, TokenPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Segments.Count > 0
            && path.Segments[0].Equals("io", StringComparison.OrdinalIgnoreCase))
        {
            // Physical I/O is always critical, even if a future catalog entry
            // accidentally associates it with a less restrictive scope.
            return WriteRisk.Critical;
        }

        return scope switch
        {
            SyncScope.Cust => WriteRisk.Cosmetic,
            SyncScope.In
                or SyncScope.Filter
                or SyncScope.Delay
                or SyncScope.Gate
                or SyncScope.Dyn
                or SyncScope.Eq
                or SyncScope.Pan => WriteRisk.AudibleProcessing,
            SyncScope.Main1
                or SyncScope.Main2
                or SyncScope.Main3
                or SyncScope.Main4
                or SyncScope.Send
                or SyncScope.Fdr
                or SyncScope.Mute => WriteRisk.MixControl,
            SyncScope.Tags
                or SyncScope.Conn
                or SyncScope.Pre
                or SyncScope.Post
                or SyncScope.Config => WriteRisk.Routing,
            _ => WriteRisk.Critical,
        };
    }
}

/// <summary>
/// Produces deterministic, fail-closed target writes from source events or snapshots.
/// </summary>
public sealed class WritePlanner
{
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a planner with an optional deterministic time provider.</summary>
    /// <param name="timeProvider">The clock used when no explicit planning timestamp is supplied.</param>
    public WritePlanner(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Maps, filters, coalesces, classifies, and safely orders a set of fresh source changes.
    /// </summary>
    /// <param name="changes">Fresh source changes in observation order.</param>
    /// <param name="mapping">Validated regular and auxiliary channel mapping.</param>
    /// <param name="enabledScopes">The global WING scopes allowed for this session.</param>
    /// <param name="safety">The write safety policy.</param>
    /// <param name="plannedAt">The timestamp used for echo fingerprints.</param>
    /// <returns>An ordered immutable plan.</returns>
    public WritePlan Plan(
        IEnumerable<SyncChange> changes,
        ChannelMapping mapping,
        IEnumerable<SyncScope> enabledScopes,
        SafetySettings safety,
        DateTimeOffset? plannedAt = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(enabledScopes);
        ArgumentNullException.ThrowIfNull(safety);

        var enabled = enabledScopes.ToHashSet();
        var issues = new List<PlanningIssue>();
        var candidatesByTarget = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var blockedDynamicSourceGroups = new HashSet<string>(StringComparer.Ordinal);
        var inputOrder = 0;
        var superseded = 0;

        // First turn each source observation into at most one policy-approved
        // target candidate. Rejected observations remain visible as plan issues.
        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            ArgumentNullException.ThrowIfNull(change.SourcePath);
            var rewrite = ScopeCatalog.Rewrite(change.SourcePath, change.Value, mapping);
            if (!rewrite.IsSuccess)
            {
                issues.Add(new PlanningIssue(
                    MapIssueCode(rewrite.Status),
                    change.SourcePath,
                    rewrite.Message ?? "The token could not be rewritten safely."));
                if (rewrite.Status == TokenRewriteStatus.UnresolvedSidechainReference &&
                    GetDynamicSourceGroup(change.SourcePath) is { } blockedGroup)
                {
                    blockedDynamicSourceGroups.Add(blockedGroup);
                }

                inputOrder++;
                continue;
            }

            var scope = rewrite.Scope!.Value;
            if (!enabled.Contains(scope))
            {
                issues.Add(new PlanningIssue(
                    PlanningIssueCode.DisabledScope,
                    change.SourcePath,
                    $"Scope '{scope}' is not enabled."));
                inputOrder++;
                continue;
            }

            var targetPath = rewrite.TargetPath!;
            var risk = WriteRiskClassifier.Classify(scope, targetPath);
            var disposition = DetermineDisposition(scope, safety);
            var candidate = new Candidate(
                change.SourcePath,
                targetPath,
                rewrite.TargetValue!.Value,
                scope,
                risk,
                disposition,
                inputOrder,
                isSafetyGuard: false);

            if (candidatesByTarget.ContainsKey(targetPath.ToString()))
            {
                superseded++;
            }

            candidatesByTarget[targetPath.ToString()] = candidate;
            inputOrder++;
        }

        // An unresolved sidechain reference makes the whole processor group
        // unsafe: applying only its remaining leaves could create a mixed model.
        var candidates = candidatesByTarget.Values
            .Where(candidate =>
                GetDynamicSourceGroup(candidate.SourcePath) is not { } sourceGroup ||
                !blockedDynamicSourceGroups.Contains(sourceGroup))
            .ToList();
        AddModelSafetyGuards(candidates);

        // Preserve the first-seen order between independent processor groups,
        // while enforcing guard -> model -> settings -> restore within a group.
        var groupOrder = candidates
            .GroupBy(static candidate => candidate.GroupKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Min(static candidate => candidate.InputOrder),
                StringComparer.Ordinal);

        candidates.Sort((left, right) =>
        {
            var comparison = groupOrder[left.GroupKey].CompareTo(groupOrder[right.GroupKey]);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.ModelPhase.CompareTo(right.ModelPhase);
            return comparison != 0
                ? comparison
                : left.InputOrder.CompareTo(right.InputOrder);
        });

        var timestamp = plannedAt ?? timeProvider.GetUtcNow();
        var writes = candidates
            .Select((candidate, sequence) => new PlannedWrite(
                sequence,
                candidate.SourcePath,
                candidate.TargetPath,
                candidate.Value,
                candidate.Scope,
                candidate.Risk,
                candidate.Disposition,
                safety.RequireReadback,
                candidate.IsSafetyGuard,
                EchoFingerprint.Create(
                    candidate.TargetPath,
                    candidate.Value,
                    timestamp,
                    safety.EchoSuppressionWindow,
                    safety.FloatTolerance)))
            .ToArray();

        return new WritePlan(writes, issues, superseded);
    }

    private static WriteDisposition DetermineDisposition(
        SyncScope scope,
        SafetySettings safety)
    {
        if (safety.DryRun)
        {
            return WriteDisposition.DryRun;
        }

        if (ConfigValidator.IsHighRiskScope(scope) && !safety.AllowHighRiskWrites)
        {
            return WriteDisposition.BlockedBySafety;
        }

        return WriteDisposition.Execute;
    }

    private static PlanningIssueCode MapIssueCode(TokenRewriteStatus status) =>
        status switch
        {
            TokenRewriteStatus.ReadOnlyToken => PlanningIssueCode.ReadOnlyToken,
            TokenRewriteStatus.UnsupportedToken => PlanningIssueCode.UnsupportedToken,
            TokenRewriteStatus.MissingChannelMapping => PlanningIssueCode.MissingChannelMapping,
            TokenRewriteStatus.UnresolvedSidechainReference =>
                PlanningIssueCode.UnresolvedSidechainReference,
            _ => PlanningIssueCode.UnsupportedToken,
        };

    private static void AddModelSafetyGuards(ICollection<Candidate> candidates)
    {
        var nextSyntheticOrder = candidates.Count == 0
            ? 0
            : candidates.Max(static candidate => candidate.InputOrder) + 1;
        var dynamicGroups = candidates
            .Where(static candidate => candidate.IsDynamicGroup)
            .GroupBy(static candidate => candidate.GroupKey, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in dynamicGroups)
        {
            if (!group.Any(static candidate =>
                    IsGuardedMutationPath(candidate.TargetPath)))
            {
                continue;
            }

            // Reuse the group's final enable value to synthesize a temporary
            // off/bypass write; execution restores the original write last.
            foreach (var enablingWrite in group.Where(static candidate => candidate.IsEnablingWrite).ToArray())
            {
                candidates.Add(enablingWrite.CreateGuard(nextSyntheticOrder++));
            }
        }
    }

    private static string? GetDynamicSourceGroup(TokenPath path)
    {
        if (path.Segments.Count < 4)
        {
            return null;
        }

        var section = path.Segments[2].ToLowerInvariant() switch
        {
            "gate" or "gatesc" => "gate",
            "dyn" or "dynxo" or "dynsc" => "dyn",
            _ => null,
        };
        return section is null
            ? null
            : $"/{path.Segments[0]}/{path.Segments[1]}/{section}";
    }

    private static bool IsDelayPath(TokenPath path) =>
        path.Segments.Count >= 5 &&
        path.Segments[2].Equals("in", StringComparison.OrdinalIgnoreCase) &&
        path.Segments[3].Equals("set", StringComparison.OrdinalIgnoreCase) &&
        (path.Leaf.Equals("dlymode", StringComparison.OrdinalIgnoreCase) ||
         path.Leaf.Equals("dly", StringComparison.OrdinalIgnoreCase) ||
         path.Leaf.Equals("dlyon", StringComparison.OrdinalIgnoreCase));

    private static bool IsGuardedMutationPath(TokenPath path) =>
        path.Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) ||
        (IsDelayPath(path) &&
         !path.Leaf.Equals("dlyon", StringComparison.OrdinalIgnoreCase));

    private sealed class Candidate
    {
        public Candidate(
            TokenPath sourcePath,
            TokenPath targetPath,
            WingValue value,
            SyncScope scope,
            WriteRisk risk,
            WriteDisposition disposition,
            int inputOrder,
            bool isSafetyGuard)
        {
            SourcePath = sourcePath;
            TargetPath = targetPath;
            Value = value;
            Scope = scope;
            Risk = risk;
            Disposition = disposition;
            InputOrder = inputOrder;
            IsSafetyGuard = isSafetyGuard;
            GroupKey = GetGroupKey(targetPath, inputOrder, out var dynamicGroup);
            IsDynamicGroup = dynamicGroup;
            ModelPhase = GetModelPhase(targetPath, value, isSafetyGuard);
        }

        public TokenPath SourcePath { get; }

        public TokenPath TargetPath { get; }

        public WingValue Value { get; }

        public SyncScope Scope { get; }

        public WriteRisk Risk { get; }

        public WriteDisposition Disposition { get; }

        public int InputOrder { get; }

        public bool IsSafetyGuard { get; }

        public string GroupKey { get; }

        public bool IsDynamicGroup { get; }

        public int ModelPhase { get; }

        public bool IsEnablingWrite =>
            IsEnablePath(TargetPath)
            && IsEnabledState(TargetPath.Leaf, Value);

        public Candidate CreateGuard(int inputOrder)
        {
            var guardValue = Value.Type switch
            {
                WingValueType.I => WingValue.FromInt32(Value.AsInt32() == 0 ? 1 : 0),
                WingValueType.F => WingValue.FromFloat(Value.AsFloat() == 0F ? 1F : 0F),
                WingValueType.S => WingValue.FromString(
                    IsBypassLeaf(TargetPath.Leaf) ? "ON" : "OFF"),
                _ => Value,
            };

            return new Candidate(
                SourcePath,
                TargetPath,
                guardValue,
                Scope,
                Risk,
                Disposition,
                inputOrder,
                isSafetyGuard: true);
        }

        private static string GetGroupKey(
            TokenPath path,
            int inputOrder,
            out bool dynamicGroup)
        {
            dynamicGroup = false;
            if (IsDelayPath(path))
            {
                dynamicGroup = true;
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"/{path.Segments[0]}/{path.Segments[1]}/delay");
            }

            if (path.Segments.Count < 4)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"$single:{inputOrder}:{path}");
            }

            var section = path.Segments[2];
            var normalizedSection = section.ToUpperInvariant() switch
            {
                "GATESC" => "gate",
                "DYNXO" or "DYNSC" => "dyn",
                "FLT" => "flt",
                "GATE" => "gate",
                "DYN" => "dyn",
                "EQ" => "eq",
                "PEQ" => "peq",
                _ => default,
            };
            if (normalizedSection is null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"$single:{inputOrder}:{path}");
            }

            dynamicGroup = true;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"/{path.Segments[0]}/{path.Segments[1]}/{normalizedSection}");
        }

        private static int GetModelPhase(
            TokenPath path,
            WingValue value,
            bool isSafetyGuard)
        {
            if (isSafetyGuard)
            {
                return 0;
            }

            if (path.Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) ||
                (IsDelayPath(path) &&
                 path.Leaf.Equals("dlymode", StringComparison.OrdinalIgnoreCase)))
            {
                return 1;
            }

            if (IsEnablePath(path))
            {
                return IsEnabledState(path.Leaf, value) ? 3 : 0;
            }

            return 2;
        }

        private static bool IsEnablePath(TokenPath path)
        {
            if (path.Leaf.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                IsBypassLeaf(path.Leaf))
            {
                return true;
            }

            if (IsDelayPath(path) &&
                path.Leaf.Equals("dlyon", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return path.Segments.Count >= 4 &&
                path.Segments[2].Equals("flt", StringComparison.OrdinalIgnoreCase) &&
                (path.Leaf.Equals("lc", StringComparison.OrdinalIgnoreCase) ||
                 path.Leaf.Equals("hc", StringComparison.OrdinalIgnoreCase) ||
                 path.Leaf.Equals("tf", StringComparison.OrdinalIgnoreCase) ||
                 path.Leaf.Equals("hpfon", StringComparison.OrdinalIgnoreCase) ||
                 path.Leaf.Equals("lpfon", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsBypassLeaf(string leaf) =>
            leaf.Equals("byp", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("bypass", StringComparison.OrdinalIgnoreCase);

        private static bool IsEnabledState(string leaf, WingValue value)
        {
            var state = value.Type switch
            {
                WingValueType.I => value.AsInt32() != 0,
                WingValueType.F => value.AsFloat() != 0F,
                WingValueType.S => ParseBoolean(value.AsString()),
                _ => false,
            };

            return IsBypassLeaf(leaf) ? !state : state;
        }

        private static bool ParseBoolean(string value) =>
            value.Equals("ON", StringComparison.OrdinalIgnoreCase)
            || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("YES", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal);
    }
}
