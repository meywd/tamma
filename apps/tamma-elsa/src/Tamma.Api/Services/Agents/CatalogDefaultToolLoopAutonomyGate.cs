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
/// with <c>Enforceable = false</c> is likewise never denied.</para>
/// </summary>
public sealed class CatalogDefaultToolLoopAutonomyGate : IToolLoopAutonomyGate
{
    private readonly int _dial;
    private readonly Func<ActionDescriptor, int>? _minAutonomyOverride;
    private readonly ILogger<CatalogDefaultToolLoopAutonomyGate>? _logger;

    /// <summary>Production constructor — shipped dial default, shipped catalog thresholds.</summary>
    public CatalogDefaultToolLoopAutonomyGate(
        ILogger<CatalogDefaultToolLoopAutonomyGate>? logger = null)
        : this(AcceptanceDefaults.DefaultAutonomyLevel, minAutonomyOverride: null, logger)
    {
    }

    /// <summary>
    /// Test/rehearsal seam (InternalsVisibleTo): pins the decision semantics for
    /// dial positions and thresholds the shipped defaults cannot reach (the
    /// shipped tool table is all-<see cref="AutonomyDial.Min"/> by design).
    /// 43-5's resolver-backed gate supersedes this seam.
    /// </summary>
    internal CatalogDefaultToolLoopAutonomyGate(
        int dial,
        Func<ActionDescriptor, int>? minAutonomyOverride = null,
        ILogger<CatalogDefaultToolLoopAutonomyGate>? logger = null)
    {
        _dial = dial;
        _minAutonomyOverride = minAutonomyOverride;
        _logger = logger;
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

        if (!descriptor.Enforceable)
        {
            return new ToolLoopGateDecision(
                ToolLoopGateOutcome.Allowed, key, null, _dial, "not-enforceable");
        }

        var minAutonomy = _minAutonomyOverride?.Invoke(descriptor) ?? descriptor.DefaultMinAutonomy;
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
    /// THE v1 dial semantics, in one place: automated iff
    /// <c>dial &gt;= minAutonomy</c>. <see cref="AutonomyDial.AlwaysHuman"/> is
    /// strictly above <see cref="AutonomyDial.Max"/>, so it blocks at every
    /// valid dial position with no special case.
    /// </summary>
    internal static bool IsAutomated(int minAutonomy, int dial) => dial >= minAutonomy;

    private static bool TryResolveKey(string toolName, string? argumentsJson, out ActionKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(toolName)) return false;

        // git_operations is the one argument-bound split (43-2 AC8): grade by
        // the call's subcommand; bare/unparseable grades as .write (fail-safe).
        if (string.Equals(toolName, "git_operations", StringComparison.OrdinalIgnoreCase))
        {
            return ToolNameAliases.TryResolveGit(TryReadSubcommand(argumentsJson), out key);
        }

        return ToolNameAliases.TryResolve(toolName, out key);
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
