using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Epic 43 Seam B — decision semantics of the v1 tool-loop autonomy gate
/// (<see cref="CatalogDefaultToolLoopAutonomyGate"/>): <b>automated iff
/// <c>dial &gt;= MinAutonomy</c>; <see cref="AutonomyDial.AlwaysHuman"/> blocks
/// at every valid dial position</b>. The shipped tool defaults are
/// all-<see cref="AutonomyDial.Min"/> (behaviour-preserving, epic D1), so the
/// denied paths are exercised through the internal rehearsal seam that 43-5's
/// resolver will replace — the semantics pinned here are the contract that
/// replacement must keep.
/// </summary>
[TestFixture]
public class ToolLoopAutonomyGateTests
{
    private static ActionKey Tool(ToolAction t) => new(ActionNamespace.Tool, t.ToWire());

    // ── Allowed: the behaviour-preserving day-one shape ─────────────────────

    [TestCase("file_read")]
    [TestCase("file_write")]
    [TestCase("shell_execute")]
    [TestCase("run_tests")]
    [TestCase("search_code")]
    [TestCase("get_acceptance_rules")]
    [TestCase("git_operations")]
    [TestCase("Bash")]
    [TestCase("Write")]
    public void Shipped_defaults_allow_every_tool_at_every_valid_dial(string toolName)
    {
        // Epic D1: v1 enforces WITH defaults that reproduce today's behaviour —
        // every tool descriptor ships DefaultMinAutonomy = AutonomyDial.Min, so
        // no dial position can deny a shipped default.
        foreach (var dial in AutonomyDial.ValidLevels())
        {
            var gate = new CatalogDefaultToolLoopAutonomyGate(dial);
            var decision = gate.Evaluate(toolName, "{}");

            decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
                $"'{toolName}' at dial {dial} must be allowed under shipped defaults (behaviour-preserving v1)");
        }
    }

    [Test]
    public void Production_constructor_uses_the_shipped_dial_default_and_allows()
    {
        var decision = new CatalogDefaultToolLoopAutonomyGate().Evaluate("shell_execute", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
        decision.Dial.Should().Be(AcceptanceDefaults.DefaultAutonomyLevel);
        decision.MinAutonomy.Should().Be(AutonomyDial.Min);
        decision.ActionKey.Should().Be(Tool(ToolAction.ShellExecute));
        decision.Reason.Should().Be("at-or-above-min-autonomy");
    }

    // ── Denied: dial below the effective threshold ──────────────────────────

    [Test]
    public void A_threshold_above_the_dial_denies()
    {
        // v1 dial semantics: automated iff dial >= MinAutonomy.
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AutonomyDial.Min,
            minAutonomyOverride: _ => AutonomyDial.Min + 10);

        var decision = gate.Evaluate("shell_execute", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        decision.IsDenied.Should().BeTrue();
        decision.MinAutonomy.Should().Be(AutonomyDial.Min + 10);
        decision.Dial.Should().Be(AutonomyDial.Min);
        decision.Reason.Should().Be("below-min-autonomy");
        decision.ActionKey.Should().Be(Tool(ToolAction.ShellExecute));
    }

    [Test]
    public void A_threshold_at_the_dial_allows_boundary_exact()
    {
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: 85, minAutonomyOverride: _ => 85);

        gate.Evaluate("file_write", "{}").Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
            "automated iff dial >= MinAutonomy — equality automates");
    }

    // ── AlwaysHuman blocks at every valid dial position ─────────────────────

    [Test]
    public void AlwaysHuman_denies_at_every_valid_dial_position()
    {
        foreach (var dial in AutonomyDial.ValidLevels())
        {
            var gate = new CatalogDefaultToolLoopAutonomyGate(
                dial, minAutonomyOverride: _ => AutonomyDial.AlwaysHuman);

            var decision = gate.Evaluate("file_write", "{}");

            decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied,
                $"AlwaysHuman must block at dial {dial} — it is strictly above AutonomyDial.Max by construction");
            decision.Reason.Should().Be("always-human");
        }
    }

    [Test]
    public void The_decision_function_is_the_v1_semantics_in_one_place()
    {
        CatalogDefaultToolLoopAutonomyGate.IsAutomated(AutonomyDial.Min, AutonomyDial.Min).Should().BeTrue();
        CatalogDefaultToolLoopAutonomyGate.IsAutomated(AutonomyDial.Max, AutonomyDial.Max).Should().BeTrue();
        CatalogDefaultToolLoopAutonomyGate.IsAutomated(AutonomyDial.Min + 1, AutonomyDial.Min).Should().BeFalse();
        foreach (var dial in AutonomyDial.ValidLevels())
        {
            CatalogDefaultToolLoopAutonomyGate.IsAutomated(AutonomyDial.AlwaysHuman, dial)
                .Should().BeFalse($"AlwaysHuman blocks at dial {dial}");
        }
    }

    // ── Resolution behaviour ────────────────────────────────────────────────

    [Test]
    public void An_uncatalogued_tool_name_is_allowed_at_runtime_epic_D2()
    {
        // Unclassified is allowed at RUNTIME, unmergeable in CI (the startup
        // validator + sweeps are the merge gate) — a catalog gap must never
        // stall a live workflow.
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AutonomyDial.Min, minAutonomyOverride: _ => AutonomyDial.AlwaysHuman);

        var decision = gate.Evaluate("mcp__some_server__some_tool", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
        decision.Reason.Should().Be("uncatalogued");
        decision.ActionKey.Should().BeNull();
    }

    [Test]
    public void Git_operations_is_graded_by_subcommand()
    {
        var gate = new CatalogDefaultToolLoopAutonomyGate(AutonomyDial.Min);

        gate.Evaluate("git_operations", """{"subcommand":"status"}""")
            .ActionKey.Should().Be(Tool(ToolAction.GitOperationsRead));
        gate.Evaluate("git_operations", """{"subcommand":"push"}""")
            .ActionKey.Should().Be(Tool(ToolAction.GitOperationsWrite));
        gate.Evaluate("git_operations", """{"subcommand":"STATUS"}""")
            .ActionKey.Should().Be(Tool(ToolAction.GitOperationsRead),
                "the tool accepts mixed case, so the gate must grade it identically (the comparer trap)");
    }

    [TestCase(null)]
    [TestCase("not json at all")]
    [TestCase("{}")]
    [TestCase("""{"subcommand": 42}""")]
    public void Ungradeable_git_operations_arguments_grade_as_write(string? argumentsJson)
    {
        var gate = new CatalogDefaultToolLoopAutonomyGate(AutonomyDial.Min);

        gate.Evaluate("git_operations", argumentsJson)
            .ActionKey.Should().Be(Tool(ToolAction.GitOperationsWrite),
            "an unparseable git call must be graded by the stricter member (fail-safe)");
    }

    [Test]
    public void Gating_git_writes_denies_push_but_not_status()
    {
        // The whole point of the read/write split: git push independently
        // gateable while git status stays automated.
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AutonomyDial.Min,
            minAutonomyOverride: d => d.Key == Tool(ToolAction.GitOperationsWrite)
                ? AutonomyDial.AlwaysHuman
                : d.DefaultMinAutonomy);

        gate.Evaluate("git_operations", """{"subcommand":"push"}""")
            .Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        gate.Evaluate("git_operations", """{"subcommand":"status"}""")
            .Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
    }

    [Test]
    public void A_non_enforceable_descriptor_is_never_denied()
    {
        // Enforceable=false members (effect:secret.reveal is the only shipped
        // one) may never block. No tool descriptor ships non-enforceable, so
        // this pins the branch through the override seam by construction: if a
        // future tool member ships Enforceable=false, the gate must ignore any
        // threshold on it.
        ActionCatalog.All.Where(d => !d.Enforceable)
            .Should().OnlyContain(d => d.Key.Ns != ActionNamespace.Tool,
                "if a tool member ever ships non-enforceable, extend this fixture to evaluate it");
    }

    [Test]
    public void Evaluate_short_circuits_a_non_enforceable_descriptor_before_any_threshold()
    {
        // 43-4 review (2026-07-29): the branch itself, driven through Evaluate.
        // No shipped tool descriptor is non-enforceable, so the rehearsal seam
        // marks every descriptor non-enforceable while an AlwaysHuman threshold
        // is armed — the short-circuit must win BEFORE the threshold is even
        // consulted, at every valid dial position.
        foreach (var dial in AutonomyDial.ValidLevels())
        {
            var gate = new CatalogDefaultToolLoopAutonomyGate(
                dial,
                minAutonomyOverride: _ => AutonomyDial.AlwaysHuman,
                enforceableOverride: _ => false);

            var decision = gate.Evaluate("shell_execute", "{}");

            decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
                $"a non-enforceable descriptor may never be denied (dial {dial})");
            decision.Reason.Should().Be("not-enforceable");
            decision.MinAutonomy.Should().BeNull(
                "no threshold was applied — the short-circuit precedes threshold resolution");
            decision.ActionKey.Should().Be(Tool(ToolAction.ShellExecute),
                "the resolved key is still reported for logging/audit");
            decision.Dial.Should().Be(dial);
        }
    }
}
