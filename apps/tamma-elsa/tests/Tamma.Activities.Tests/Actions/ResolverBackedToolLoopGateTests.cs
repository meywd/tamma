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
        => new(
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
        Gate(GovernancePolicySnapshot.Unavailable).Evaluate("mcp__server__tool", "{}")
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

        var decision = Gate(snapshot).Evaluate("mcp__server__tool", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
        decision.Reason.Should().Be("uncatalogued");
    }
}
