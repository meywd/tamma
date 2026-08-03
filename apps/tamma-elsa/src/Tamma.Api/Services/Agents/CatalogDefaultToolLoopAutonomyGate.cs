using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// The v1 Seam B gate: catalog-shipped defaults, v1 dial semantics —
/// <b>automated iff <c>dial &gt;= MinAutonomy</c>; <see cref="AutonomyDial.AlwaysHuman"/>
/// blocks at every valid dial position</b> (epic decision D1: v1 ENFORCES, with
/// defaults that reproduce today's behaviour exactly).
///
/// <para><b>Behaviour-preserving by construction:</b> every shipped
/// <c>tool:*</c> descriptor carries <c>DefaultMinAutonomy = AutonomyDial.Min</c>
/// (43-3 D4), and the v1 dial is the shipped default
/// (<see cref="AcceptanceDefaults.DefaultAutonomyLevel"/> = <see cref="AutonomyDial.Min"/>),
/// so on day one every tool call this gate sees is <c>Allowed</c> — the gate is
/// live, and it bites the moment policy makes a threshold exceed the dial.
/// Story 43-5 replaces the threshold/dial legs with the resolver ladder
/// (platform ceiling → legacy always-escalate floor → principal ladder) behind
/// the same <see cref="IToolLoopAutonomyGate"/> contract; the internal seams
/// here are its rehearsal hooks and the tests' red path.</para>
///
/// <para>Resolution tolerance (epic decision D2): a tool name with no catalog
/// member is <c>Allowed</c> at runtime — unclassified is unmergeable in CI (the
/// 43-4 startup validator + sweeps), never a production stall. A descriptor
/// with <c>Enforceable = false</c> is likewise never denied.
/// <b>The one named exception is the <c>mcp__*</c> family</b>, which is no longer
/// uncatalogued: <c>ToolNameAliases</c> resolves it to
/// <c>effect:mcp.tool.invoke</c>, which ships
/// <see cref="AutonomyDial.AlwaysHuman"/>. D2's bargain is "allowed at runtime
/// BECAUSE unmergeable in CI", and for MCP the CI half cannot exist — no harness
/// can enumerate a remote server's tools — so the runtime half is not owed
/// either. Nothing regresses: no MCP executor is registered, so such a call was
/// already going to come back "Unknown tool".</para>
///
/// <para><b>Break-glass (43-5 F11).</b> With
/// <see cref="Actions.IGovernanceBreakGlass"/> engaged, a never-loaded snapshot
/// resolves on shipped defaults instead of denying. It is not an allow: the
/// shipped threshold is then applied normally, so an <c>AlwaysHuman</c> default
/// still blocks. Every bypassed call logs at ERROR here and is audited by
/// <see cref="InlineToolLoopRunner"/> (this gate is sync by design, so the
/// durable append rides the runner's async path).</para>
/// </summary>
public sealed class CatalogDefaultToolLoopAutonomyGate : IToolLoopAutonomyGate
{
    private readonly int _dial;
    private readonly Func<ActionDescriptor, int>? _minAutonomyOverride;
    private readonly Func<ActionDescriptor, bool>? _enforceableOverride;
    private readonly Actions.IGovernancePolicySnapshotProvider? _snapshots;
    private readonly Tamma.Data.ITenantContext? _tenantContext;
    private readonly Actions.IGovernanceBreakGlass? _breakGlass;
    private readonly ILogger<CatalogDefaultToolLoopAutonomyGate>? _logger;
    // Story 42-10 (AC6, D7) — the configured secret-bearing paths the shell
    // secret-read screen grades against; defaults to ShellSecretReadScreen's.
    private readonly IReadOnlyList<string>? _shellSecretPaths;

    /// <summary>Production constructor — shipped dial default, shipped catalog thresholds.</summary>
    public CatalogDefaultToolLoopAutonomyGate(
        ILogger<CatalogDefaultToolLoopAutonomyGate>? logger = null)
        : this(AcceptanceDefaults.DefaultAutonomyLevel, minAutonomyOverride: null, logger)
    {
    }

    /// <summary>
    /// Story 43-5 — the RESOLVER-BACKED production constructor (the data-source
    /// seam the 43-4 doc promised): thresholds come from the 43-5 assignment
    /// ladder (platform ceiling → principal action row → group row → shipped
    /// default) via the sync <see cref="Actions.IGovernancePolicySnapshotProvider"/>
    /// snapshot, projected for the ambient principal
    /// (<see cref="Tamma.Data.ITenantContext"/> in SaaS; the collapsed sole-user
    /// rows in single-user mode). With ZERO assignment rows the ladder returns
    /// every descriptor's <c>DefaultMinAutonomy</c> — byte-identical to the
    /// 43-4 catalog-default behaviour, pinned by
    /// <c>ResolverBackedToolLoopGateTests</c>.
    ///
    /// <para>The DIAL stays <see cref="AcceptanceDefaults.DefaultAutonomyLevel"/>
    /// on this seam, exactly as 43-4 shipped it: the per-principal base-row dial
    /// is an async tenant-DB read that has no place on a sync per-tool-call
    /// path — it rides <c>IAutonomyGate</c> (43-9's seams). The legacy
    /// always-escalate floor is structurally irrelevant here (it exists only on
    /// the agent-action/document-type planes; this seam gates <c>tool:*</c>).</para>
    /// </summary>
    public CatalogDefaultToolLoopAutonomyGate(
        Actions.IGovernancePolicySnapshotProvider snapshots,
        Tamma.Data.ITenantContext tenantContext,
        ILogger<CatalogDefaultToolLoopAutonomyGate>? logger = null,
        Actions.IGovernanceBreakGlass? breakGlass = null,
        IReadOnlyList<string>? shellSecretPaths = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(tenantContext);
        _dial = AcceptanceDefaults.DefaultAutonomyLevel;
        _snapshots = snapshots;
        _tenantContext = tenantContext;
        _breakGlass = breakGlass;
        _logger = logger;
        _shellSecretPaths = shellSecretPaths;
    }

    /// <summary>
    /// Test/rehearsal seam (InternalsVisibleTo): pins the decision semantics for
    /// dial positions and thresholds the shipped defaults cannot reach (the
    /// shipped tool table is all-<see cref="AutonomyDial.Min"/> by design).
    /// <paramref name="enforceableOverride"/> likewise pins the
    /// <c>Enforceable = false</c> short-circuit, which no shipped TOOL
    /// descriptor can reach naturally (43-4 review, 2026-07-29). 43-5's
    /// resolver-backed gate supersedes this seam.
    /// </summary>
    internal CatalogDefaultToolLoopAutonomyGate(
        int dial,
        Func<ActionDescriptor, int>? minAutonomyOverride = null,
        ILogger<CatalogDefaultToolLoopAutonomyGate>? logger = null,
        Func<ActionDescriptor, bool>? enforceableOverride = null)
    {
        _dial = dial;
        _minAutonomyOverride = minAutonomyOverride;
        _logger = logger;
        _enforceableOverride = enforceableOverride;
    }

    /// <inheritdoc />
    public ToolLoopGateDecision Evaluate(string toolName, string? argumentsJson)
    {
        if (!TryResolveKey(toolName, argumentsJson, out var key))
        {
            // Epic D2 — unclassified is allowed at RUNTIME (and unmergeable in
            // CI via the startup validator + sweeps). Not a silent pass: logged.
            _logger?.LogWarning(
                "Autonomy gate: tool name '{ToolName}' resolves to no catalog member; allowing (epic 43 D2 runtime tolerance)",
                toolName);
            return new ToolLoopGateDecision(
                ToolLoopGateOutcome.Allowed, null, null, _dial, "uncatalogued");
        }

        if (!ActionCatalog.TryGet(key, out var descriptor) || descriptor is null)
        {
            return new ToolLoopGateDecision(
                ToolLoopGateOutcome.Allowed, key, null, _dial, "uncatalogued");
        }

        if (!(_enforceableOverride?.Invoke(descriptor) ?? descriptor.Enforceable))
        {
            return new ToolLoopGateDecision(
                ToolLoopGateOutcome.Allowed, key, null, _dial, "not-enforceable");
        }

        var (minAutonomy, source) = ResolveMinAutonomy(descriptor, out var breakGlass);
        if (source == ActionAssignmentSource.BreakGlass)
        {
            // 43-5 F11 — the operator's break-glass override suspended the F6
            // fail-closed substitution for this call. NOT a pass: the shipped
            // threshold below still applies (effect:mcp.tool.invoke and the three
            // human-acceptor document types still block), and only the SUBSTITUTED
            // AlwaysHuman was skipped. Loud at ERROR on every bypassed call; the
            // durable audit row is emitted by InlineToolLoopRunner, which is on
            // an async path (this gate is sync by design — 43-5 AC12).
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS BYPASS (tool loop): the governance policy snapshot has "
                + "never loaded, and the break-glass override (engaged until {ExpiresAt:O}, "
                + "reason: {Reason}) let this call resolve on shipped defaults instead of failing "
                + "closed: Action={ActionKey}, MinAutonomy={MinAutonomy}, Dial={Dial}",
                breakGlass?.ExpiresAtUtc, breakGlass?.ReasonOrUnspecified,
                key.ToWire(), minAutonomy, _dial);

            return new ToolLoopGateDecision(
                IsAutomated(minAutonomy, _dial)
                    ? ToolLoopGateOutcome.Allowed
                    : ToolLoopGateOutcome.Denied,
                key, minAutonomy, _dial,
                IsAutomated(minAutonomy, _dial)
                    ? AutonomyGateEvaluator.ReasonBreakGlassBypass
                    : minAutonomy == AutonomyDial.AlwaysHuman ? "always-human" : "below-min-autonomy",
                breakGlass);
        }

        if (source == ActionAssignmentSource.Unavailable)
        {
            // 43-5 F6 — the governance snapshot has never loaded, so "no ceiling
            // row" is ignorance, not policy. Fail CLOSED and say so distinctly:
            // this is NOT the same event as "policy says this needs a person".
            _logger?.LogError(
                "Autonomy gate DENIED tool call because the governance policy snapshot has "
                + "never loaded (fail-closed, 43-5 F6): Action={ActionKey}, Dial={Dial}",
                key.ToWire(), _dial);
            return new ToolLoopGateDecision(
                ToolLoopGateOutcome.Denied, key, minAutonomy, _dial,
                AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable);
        }

        if (IsAutomated(minAutonomy, _dial))
        {
            return new ToolLoopGateDecision(
                ToolLoopGateOutcome.Allowed, key, minAutonomy, _dial, "at-or-above-min-autonomy");
        }

        // A denial under enforcement is never swallowed silently (epic audit
        // rule) — the full 43-9 audit event family lands with the seams story;
        // until then the structured warning is the trail.
        _logger?.LogWarning(
            "Autonomy gate DENIED tool call: Action={ActionKey}, MinAutonomy={MinAutonomy}, Dial={Dial}",
            key.ToWire(), minAutonomy, _dial);
        return new ToolLoopGateDecision(
            ToolLoopGateOutcome.Denied, key, minAutonomy, _dial,
            minAutonomy == AutonomyDial.AlwaysHuman ? "always-human" : "below-min-autonomy");
    }

    /// <summary>
    /// The threshold's data source (Story 43-5): the assignment ladder when the
    /// snapshot provider is wired (production DI), else the internal rehearsal
    /// seam, else the shipped catalog default — the 43-4 shape, unchanged. The
    /// returned provenance is <see cref="ActionAssignmentSource.Unavailable"/>
    /// when the snapshot has never loaded (43-5 F6).
    /// </summary>
    private (int MinAutonomy, ActionAssignmentSource Source) ResolveMinAutonomy(
        ActionDescriptor descriptor, out BreakGlassState? breakGlass)
    {
        breakGlass = null;
        if (_minAutonomyOverride is not null)
        {
            return (_minAutonomyOverride(descriptor), ActionAssignmentSource.SystemDefault);
        }
        if (_snapshots is not null)
        {
            var snapshot = _snapshots.GetSnapshotForAmbient(_tenantContext?.TenantId);
            var state = _breakGlass?.Current();
            var resolved = AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(
                descriptor, snapshot, state);
            if (resolved.Source == ActionAssignmentSource.BreakGlass)
            {
                breakGlass = state;
            }
            return resolved;
        }
        return (descriptor.DefaultMinAutonomy, ActionAssignmentSource.SystemDefault);
    }

    /// <summary>
    /// THE v1 dial semantics, in one place: automated iff
    /// <c>dial &gt;= minAutonomy</c>. <see cref="AutonomyDial.AlwaysHuman"/> is
    /// strictly above <see cref="AutonomyDial.Max"/>, so it blocks at every
    /// valid dial position with no special case.
    /// </summary>
    internal static bool IsAutomated(int minAutonomy, int dial) => dial >= minAutonomy;

    private bool TryResolveKey(string toolName, string? argumentsJson, out ActionKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(toolName)) return false;

        // TRIM before resolving (review LOW-6, 2026-07-31). The name comes off a
        // model's tool call, and both resolution routes are exact-prefix/exact-key
        // matches: `" mcp__server__tool"` missed `IsMcpToolName`'s StartsWith and
        // came back UNCATALOGUED — i.e. Allowed — where `"mcp__server__tool"` is
        // Denied. No capability exists behind an MCP name today (no executor is
        // registered), so nothing was reachable through the gap, but a governance
        // pin a leading space evades is not a pin. The whitespace guard above
        // already treats blank as unresolvable; this makes the rest of the string
        // agree with it.
        toolName = toolName.Trim();

        // git_operations is the one argument-bound split (43-2 AC8): grade by
        // the call's subcommand; bare/unparseable grades as .write (fail-safe).
        if (string.Equals(toolName, "git_operations", StringComparison.OrdinalIgnoreCase))
        {
            return ToolNameAliases.TryResolveGit(TryReadSubcommand(argumentsJson), out key);
        }

        if (!ToolNameAliases.TryResolve(toolName, out key))
        {
            return false;
        }

        // Story 42-10 (AC6, D7) — a shell command that READS a secret value is
        // reclassified from tool:shell_execute to effect:secret.read (level 90),
        // the same resolution-time split as git_operations above. Best-effort (the
        // sandbox is the control); named gaps documented on ShellSecretReadScreen.
        if (key.Ns == ActionNamespace.Tool
            && string.Equals(key.Key, ToolAction.ShellExecute.ToWire(), StringComparison.Ordinal)
            && Actions.ShellSecretReadScreen.Matches(TryReadCommand(argumentsJson), _shellSecretPaths))
        {
            key = new ActionKey(ActionNamespace.Effect, ExternalEffect.SecretRead.ToWire());
        }

        return true;
    }

    /// <summary>Read the shell tool's <c>command</c> argument (null when absent/unparseable).</summary>
    private static string? TryReadCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("command", out var cmd)
                   && cmd.ValueKind == System.Text.Json.JsonValueKind.String
                ? cmd.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? TryReadSubcommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("subcommand", out var sub)
                   && sub.ValueKind == System.Text.Json.JsonValueKind.String
                ? sub.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // unparseable arguments grade as .write via TryResolveGit's fail-safe
        }
    }
}
