using System.Collections.ObjectModel;

namespace WingSync.Core.Domain;

/// <summary>
/// Names the global WING library sections that can be synchronized.
/// </summary>
public enum SyncScope
{
    /// <summary>Channel name, color, icon, and light.</summary>
    Cust,

    /// <summary>Custom, DCA, mute, and talk tags.</summary>
    Tags,

    /// <summary>Source A/B assignment and input-selection status.</summary>
    Conn,

    /// <summary>Input trim, balance, and phase.</summary>
    In,

    /// <summary>High-pass, low-pass, tilt, and all-pass filters.</summary>
    Filter,

    /// <summary>Input delay mode, time, and enable state.</summary>
    Delay,

    /// <summary>Gate model, settings, and sidechain.</summary>
    Gate,

    /// <summary>Dynamics model, settings, crossover, and sidechain.</summary>
    Dyn,

    /// <summary>Pre-insert assignment and enable state, excluding shared FX settings.</summary>
    Pre,

    /// <summary>Post-insert assignment, mode, width, and enable state.</summary>
    Post,

    /// <summary>Channel and pre-send equalizers.</summary>
    Eq,

    /// <summary>Channel pan and width.</summary>
    Pan,

    /// <summary>Main 1 assignment and level settings.</summary>
    Main1,

    /// <summary>Main 2 assignment and level settings.</summary>
    Main2,

    /// <summary>Main 3 assignment and level settings.</summary>
    Main3,

    /// <summary>Main 4 assignment and level settings.</summary>
    Main4,

    /// <summary>Bus and matrix send settings.</summary>
    Send,

    /// <summary>Channel fader.</summary>
    Fdr,

    /// <summary>Channel mute.</summary>
    Mute,

    /// <summary>Process order, tap point, solo-safe, and monitor configuration.</summary>
    Config,
}

/// <summary>
/// Defines the single authoritative direction of synchronization.
/// </summary>
public enum SyncDirection
{
    /// <summary>No writes are allowed.</summary>
    Disabled,

    /// <summary>The front-of-house console is authoritative.</summary>
    FohToMonitor,

    /// <summary>The monitor console is authoritative.</summary>
    MonitorToFoh,

    /// <summary>Both consoles may originate changes; rejected by the safe validator.</summary>
    Bidirectional,
}

/// <summary>
/// Defines how the first difference set is handled after a fresh snapshot.
/// </summary>
public enum InitialSync
{
    /// <summary>Calculate and show a diff without executing it.</summary>
    PreviewOnly,

    /// <summary>Require an explicit operator confirmation before executing the initial diff.</summary>
    RequireConfirmation,

    /// <summary>Apply the fresh authoritative source snapshot after all safety checks pass.</summary>
    SourceWins,
}

/// <summary>
/// Controls safeguards applied while planning and verifying writes.
/// </summary>
public sealed record SafetySettings
{
    /// <summary>
    /// Initializes safety settings.
    /// </summary>
    /// <param name="dryRun">Whether all otherwise valid writes remain non-executable previews.</param>
    /// <param name="requireReadback">Whether each executed write must be observed back from the target.</param>
    /// <param name="allowHighRiskWrites">Whether routing, fader, mute, insert, main, and send writes are allowed.</param>
    /// <param name="stopOnVerificationFailure">Whether a failed readback stops subsequent writes.</param>
    /// <param name="floatTolerance">Absolute and relative tolerance used for WING-quantized floats.</param>
    /// <param name="echoSuppressionWindow">How long a target echo fingerprint remains active.</param>
    public SafetySettings(
        bool dryRun = true,
        bool requireReadback = true,
        bool allowHighRiskWrites = false,
        bool stopOnVerificationFailure = true,
        float floatTolerance = 0.0001F,
        TimeSpan? echoSuppressionWindow = null)
    {
        DryRun = dryRun;
        RequireReadback = requireReadback;
        AllowHighRiskWrites = allowHighRiskWrites;
        StopOnVerificationFailure = stopOnVerificationFailure;
        FloatTolerance = floatTolerance;
        EchoSuppressionWindow = echoSuppressionWindow ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Gets whether writes remain previews.</summary>
    public bool DryRun { get; }

    /// <summary>Gets whether writes require target readback.</summary>
    public bool RequireReadback { get; }

    /// <summary>Gets whether high-risk writes are allowed.</summary>
    public bool AllowHighRiskWrites { get; }

    /// <summary>Gets whether verification failure stops a write stream.</summary>
    public bool StopOnVerificationFailure { get; }

    /// <summary>Gets the comparison tolerance for quantized floats.</summary>
    public float FloatTolerance { get; }

    /// <summary>Gets the lifetime of locally generated echo fingerprints.</summary>
    public TimeSpan EchoSuppressionWindow { get; }

    /// <summary>Gets a new conservative set of defaults.</summary>
    public static SafetySettings SafeDefaults => new();
}

/// <summary>
/// Immutable application configuration for a two-console synchronization session.
/// </summary>
public sealed class AppConfiguration
{
    private static readonly ReadOnlyCollection<SyncScope> ConservativeScopes =
        Array.AsReadOnly(
        [
            SyncScope.Cust,
            SyncScope.Filter,
            SyncScope.Delay,
            SyncScope.Gate,
            SyncScope.Dyn,
            SyncScope.Eq,
        ]);

    private readonly ReadOnlyCollection<SyncScope> scopes;

    /// <summary>
    /// Initializes an application configuration using conservative defaults for omitted options.
    /// </summary>
    /// <param name="foh">The front-of-house console endpoint.</param>
    /// <param name="monitor">The stage-monitor console endpoint.</param>
    /// <param name="direction">The authoritative direction.</param>
    /// <param name="initialSync">The initial reconciliation policy.</param>
    /// <param name="safety">Write safety settings.</param>
    /// <param name="scopes">Enabled global WING scopes.</param>
    /// <param name="channels">Regular and auxiliary channel mapping.</param>
    public AppConfiguration(
        WingEndpoint foh,
        WingEndpoint monitor,
        SyncDirection direction = SyncDirection.FohToMonitor,
        InitialSync initialSync = InitialSync.PreviewOnly,
        SafetySettings? safety = null,
        IEnumerable<SyncScope>? scopes = null,
        ChannelMapping? channels = null)
    {
        Foh = foh ?? throw new ArgumentNullException(nameof(foh));
        Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        Direction = direction;
        InitialSync = initialSync;
        Safety = safety ?? SafetySettings.SafeDefaults;
        this.scopes = Array.AsReadOnly((scopes ?? ConservativeScopes).ToArray());
        Channels = channels ?? ChannelMapping.IdentityInputs();
    }

    /// <summary>Gets the front-of-house endpoint.</summary>
    public WingEndpoint Foh { get; }

    /// <summary>Gets the stage-monitor endpoint.</summary>
    public WingEndpoint Monitor { get; }

    /// <summary>Gets the authoritative synchronization direction.</summary>
    public SyncDirection Direction { get; }

    /// <summary>Gets the initial reconciliation policy.</summary>
    public InitialSync InitialSync { get; }

    /// <summary>Gets the write safeguards.</summary>
    public SafetySettings Safety { get; }

    /// <summary>Gets enabled WING global scopes.</summary>
    public IReadOnlyList<SyncScope> Scopes => scopes;

    /// <summary>Gets regular and auxiliary channel mappings.</summary>
    public ChannelMapping Channels { get; }

    /// <summary>Gets the conservative default scope selection.</summary>
    public static IReadOnlyList<SyncScope> SafeDefaultScopes => ConservativeScopes;
}
