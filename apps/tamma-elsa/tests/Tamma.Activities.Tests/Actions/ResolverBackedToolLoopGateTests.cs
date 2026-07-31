using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;
using Tamma.Data;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-5 — the Seam B data-source seam: the 43-4 gate class fed by the
/// 43-5 assignment ladder through <see cref="IGovernancePolicySnapshotProvider"/>.
/// Pins the behaviour-preserving property (zero rows ⇒ byte-identical to the
/// catalog-default gate at every valid dial) and that assignment rows now
/// BITE on the tool loop (tenant override, group override, platform ceiling).
/// </summary>
[TestFixture]
public class ResolverBackedToolLoopGateTests
{
    private sealed class FixedSnapshots(GovernancePolicySnapshot snapshot)
        : IGovernancePolicySnapshotProvider
    {
        public Guid? LastAmbientTenantId;

        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal) => snapshot;

        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId)
        {
            LastAmbientTenantId = tenantId;
            return snapshot;
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = tenantId;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private static CatalogDefaultToolLoopAutonomyGate Gate(
        GovernancePolicySnapshot snapshot, Guid? tenantId = null)
        => new(new FixedSnapshots(snapshot), new FixedTenantContext(tenantId));

    private static GovernancePolicySnapshot With(
        Dictionary<string, ActionAssignmentValue>? platformActions = null,
        Dictionary<string, ActionAssignmentValue>? platformGroups = null,
        Dictionary<string, ActionAssignmentValue>? principalActions = null,
        Dictionary<string, ActionAssignmentValue>? principalGroups = null)
        => GovernancePolicySnapshot.FromSuccessfulRead(
            platformActions ?? new(StringComparer.Ordinal),
            platformGroups ?? new(StringComparer.Ordinal),
            principalActions ?? new(StringComparer.Ordinal),
            principalGroups ?? new(StringComparer.Ordinal));

    // ── The behaviour-preserving parity proof ───────────────────────────────

    [TestCase("file_read")]
    [TestCase("file_write")]
    [TestCase("shell_execute")]
    [TestCase("run_tests")]
    [TestCase("search_code")]
    [TestCase("get_acceptance_rules")]
    [TestCase("git_operations")]
    [TestCase("Bash")]
    [TestCase("Write")]
    public void ZeroRows_IsByteIdenticalToTheCatalogDefaultGate(string toolName)
    {
        var resolverBacked = Gate(GovernancePolicySnapshot.Empty);
        var catalogDefault = new CatalogDefaultToolLoopAutonomyGate();

        var a = resolverBacked.Evaluate(toolName, "{}");
        var b = catalogDefault.Evaluate(toolName, "{}");

        a.Should().Be(b,
            "with zero assignment rows the ladder returns every shipped default — "
            + "the 43-5 data-source swap must change nothing on day one");
        a.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
    }

    // ── Assignment rows now bite on the tool loop ───────────────────────────

    [Test]
    public void APrincipalActionRow_AboveTheDial_Denies()
    {
        var snapshot = With(principalActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        var decision = Gate(snapshot).Evaluate("shell_execute", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        decision.MinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        decision.Reason.Should().Be("always-human");
    }

    [Test]
    public void AGroupRow_CoversItsMembers_AndAnActionRowOverridesIt()
    {
        var snapshot = With(
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:file_read"] = new(AutonomyDial.Min, null, null, null),
            },
            principalGroups: new(StringComparer.Ordinal)
            {
                ["code-read"] = new(AutonomyDial.AlwaysHuman, null, null, null),
                ["code-write"] = new(AutonomyDial.AlwaysHuman, null, null, null),
            });
        var gate = Gate(snapshot);

        gate.Evaluate("file_write", "{}").Outcome.Should().Be(ToolLoopGateOutcome.Denied,
            "the code-write group row gates file_write");
        gate.Evaluate("file_read", "{}").Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
            "the action row overrides its group outright (??, not max())");
        gate.Evaluate("search_code", "{}").Outcome.Should().Be(ToolLoopGateOutcome.Denied,
            "search_code has no action row, so the code-read group row applies");
    }

    [Test]
    public void APlatformCeiling_Binds_EvenWhenThePrincipalLowered()
    {
        var snapshot = With(
            platformActions: new(StringComparer.Ordinal)
            {
                ["tool:git_operations.write"] = new(AutonomyDial.AlwaysHuman, null, null, null),
            },
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:git_operations.write"] = new(AutonomyDial.Min, null, null, null),
            });
        var gate = Gate(snapshot);

        gate.Evaluate("git_operations", """{"subcommand":"push"}""")
            .Outcome.Should().Be(ToolLoopGateOutcome.Denied,
                "a tenant admin can never lower a platform gate (max() composition)");
        gate.Evaluate("git_operations", """{"subcommand":"status"}""")
            .Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
                "the read-graded member is not ceilinged");
    }

    [Test]
    public void TheGate_ProjectsTheAmbientTenant()
    {
        var tid = Guid.NewGuid();
        var snapshots = new FixedSnapshots(GovernancePolicySnapshot.Empty);
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            snapshots, new FixedTenantContext(tid));

        gate.Evaluate("file_write", "{}");

        snapshots.LastAmbientTenantId.Should().Be(tid,
            "policy must be resolved for the ambient principal, never a global scope");
    }

    // ── F6 (2026-07-30): the LIVE seam fails closed on a snapshot that has
    //    never loaded, and says so with its own reason ────────────────────────

    /// <summary>
    /// Seam B is the one gate that already enforces in production, so the F6
    /// posture has to hold here or it holds nowhere. A never-loaded snapshot
    /// used to be indistinguishable from an empty table — every tool call sailed
    /// through on shipped defaults while the admin's tightenings sat unread.
    /// </summary>
    [Test]
    public void AnUnavailableSnapshot_DeniesEveryCatalogedTool_WithItsOwnReason()
    {
        var gate = Gate(GovernancePolicySnapshot.Unavailable);

        foreach (var toolName in new[] { "file_read", "file_write", "shell_execute", "run_tests" })
        {
            var decision = gate.Evaluate(toolName, "{}");
            decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied,
                $"'{toolName}' must not ride shipped defaults when the policy table has "
                + "never been read (43-5 F6)");
            decision.Reason.Should().Be(
                AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable,
                "the reason must NOT be 'below-min-autonomy' — this is an outage of the "
                + "governance surface, not a policy decision");
        }

        // The loaded-and-empty table is the opposite answer, unchanged.
        Gate(GovernancePolicySnapshot.Empty).Evaluate("file_write", "{}")
            .Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
    }

    [Test]
    public void AnUnavailableSnapshot_StillAllowsUncataloguedNames_EpicD2()
    {
        // Exemplar changed from `mcp__server__tool` on 2026-07-30 — MCP names are
        // catalogued now (see the MCP section below). D2 itself is unchanged.
        Gate(GovernancePolicySnapshot.Unavailable).Evaluate("frobnicate_the_widget", "{}")
            .Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
                "a catalog gap is still never a production stall (epic D2)");
    }

    [Test]
    public void UncataloguedNames_StayAllowed_UnderTheResolverBackedGate()
    {
        var snapshot = With(principalGroups: new(StringComparer.Ordinal)
        {
            ["model-invocation"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        var decision = Gate(snapshot).Evaluate("frobnicate_the_widget", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
        decision.Reason.Should().Be("uncatalogued");
    }

    // ── The MCP governance decision (2026-07-30) through the resolver ────────

    /// <summary>
    /// An <c>mcp__*</c> name now resolves to <c>effect:mcp.tool.invoke</c>, which
    /// ships <see cref="AutonomyDial.AlwaysHuman"/>, so the live seam refuses it
    /// by default instead of passing it as uncatalogued.
    ///
    /// <para><b>Nothing that worked stops working:</b> no MCP
    /// <c>IToolExecutor</c> is registered, so before this change such a call ran
    /// to <c>ToolExecutorRegistry.GetExecutor</c> → null → "Unknown tool" fed back
    /// to the model. It is still a rejection fed back to the model; only the
    /// rejection's provenance changed, from "the registry has never heard of this"
    /// to "governance says a person decides".</para>
    /// </summary>
    [Test]
    public void AnMcpToolName_IsDeniedByDefault_AndCarriesTheMcpEffectKey()
    {
        var decision = Gate(GovernancePolicySnapshot.Empty).Evaluate("mcp__server__tool", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        decision.Reason.Should().Be("always-human");
        decision.ActionKey.Should().Be(
            new ActionKey(ActionNamespace.Effect, ExternalEffect.McpToolInvoke.ToWire()));
    }

    /// <summary>
    /// And a single admin row at the floor re-opens the whole family — the
    /// reversibility the decision rests on, proved through the real assignment
    /// ladder rather than the rehearsal seam.
    /// </summary>
    [Test]
    public void AnActionRowAtTheFloor_ReOpensMcp()
    {
        var snapshot = With(principalActions: new(StringComparer.Ordinal)
        {
            ["effect:mcp.tool.invoke"] = new(AutonomyDial.Min, null, null, null),
        });

        Gate(snapshot).Evaluate("mcp__server__tool", "{}")
            .Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
    }

    /// <summary>A platform ceiling still wins, exactly as for any other member —
    /// the MCP default is an ordinary catalog default, not a special case.</summary>
    [Test]
    public void APlatformCeiling_StillBinds_WhenAPrincipalRowReOpensMcp()
    {
        var snapshot = With(
            platformActions: new(StringComparer.Ordinal)
            {
                ["effect:mcp.tool.invoke"] = new(AutonomyDial.AlwaysHuman, null, null, null),
            },
            principalActions: new(StringComparer.Ordinal)
            {
                ["effect:mcp.tool.invoke"] = new(AutonomyDial.Min, null, null, null),
            });

        Gate(snapshot).Evaluate("mcp__server__tool", "{}")
            .Outcome.Should().Be(ToolLoopGateOutcome.Denied);
    }

    // ── F11 (2026-07-30): the break-glass override at the ONE live seam ──────

    private sealed class FixedBreakGlass(BreakGlassState state) : IGovernanceBreakGlass
    {
        public BreakGlassState Current() => state;
    }

    private static CatalogDefaultToolLoopAutonomyGate GateWithBreakGlass(
        GovernancePolicySnapshot snapshot, BreakGlassState state)
        => new(new FixedSnapshots(snapshot), new FixedTenantContext(null), null,
            new FixedBreakGlass(state));

    private static readonly BreakGlassState EngagedOverride =
        BreakGlassState.Engaged(DateTimeOffset.UtcNow.AddHours(1), "control plane unreachable");

    /// <summary>
    /// The gap F11 recorded, closed: a never-loaded snapshot denied EVERY
    /// catalogued tool, in single-user deployments as well as SaaS, with no
    /// operator lever. With the override engaged the loop keeps working, and the
    /// decision is labelled as a bypass rather than as a healthy allow.
    /// </summary>
    [Test]
    public void BreakGlassEngaged_OverADegradedSnapshot_AllowsAndCarriesTheBypassState()
    {
        var gate = GateWithBreakGlass(GovernancePolicySnapshot.Unavailable, EngagedOverride);

        foreach (var toolName in new[] { "file_read", "file_write", "shell_execute", "run_tests" })
        {
            var decision = gate.Evaluate(toolName, "{}");

            decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed, toolName);
            decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonBreakGlassBypass);
            decision.IsBreakGlassBypass.Should().BeTrue(
                "the runner emits the ACTION.GATE.BREAK_GLASS_BYPASS audit row off this");
            decision.BreakGlass!.ExpiresAtUtc.Should().Be(EngagedOverride.ExpiresAtUtc);
            decision.BreakGlass!.Reason.Should().Be("control plane unreachable");
        }
    }

    /// <summary>
    /// THE anti-backdoor pin at Seam B. Break-glass falls back to the SHIPPED
    /// DEFAULT, not to "allow" — so a member the catalog itself pins to a human
    /// (here <c>effect:mcp.tool.invoke</c>) is still refused while the override is
    /// engaged.
    /// </summary>
    [Test]
    public void BreakGlassEngaged_DoesNotOpenAnAlwaysHumanShippedDefault()
    {
        var decision = GateWithBreakGlass(GovernancePolicySnapshot.Unavailable, EngagedOverride)
            .Evaluate("mcp__server__tool", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        decision.Reason.Should().Be("always-human");
    }

    /// <summary>
    /// The other anti-backdoor half: an override cannot reach a policy row,
    /// because the bypass is sited inside the never-loaded branch and a
    /// never-loaded snapshot provably carries no rows. Proved positively — with a
    /// READ snapshot the override changes nothing, denial included.
    /// </summary>
    [Test]
    public void BreakGlassEngaged_ChangesNothing_WhenTheSnapshotWasRead()
    {
        var snapshot = With(principalActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        var denied = GateWithBreakGlass(snapshot, EngagedOverride).Evaluate("shell_execute", "{}");
        denied.Outcome.Should().Be(ToolLoopGateOutcome.Denied,
            "a successfully-read policy row is not degradation and is not bypassable");
        denied.IsBreakGlassBypass.Should().BeFalse();

        GateWithBreakGlass(snapshot, EngagedOverride).Evaluate("file_read", "{}")
            .Should().Be(Gate(snapshot).Evaluate("file_read", "{}"),
                "on a healthy read the override is inert, byte for byte");
    }

    [Test]
    public void BreakGlassNotEngaged_LeavesTheF6PostureExactlyAsItWas()
    {
        var decision = GateWithBreakGlass(
                GovernancePolicySnapshot.Unavailable, BreakGlassState.NotEngaged)
            .Evaluate("file_write", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable);
        decision.IsBreakGlassBypass.Should().BeFalse();
    }
}
