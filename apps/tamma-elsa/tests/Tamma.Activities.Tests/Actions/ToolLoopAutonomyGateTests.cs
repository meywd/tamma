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
    public void Every_tool_is_allowed_at_the_top_of_the_dial(string toolName)
    {
        // Story 43-11: "at 100 everything is automated" — every tool automates at
        // the max dial. (Below Max the dial now BITES: shell_execute sits at 80, so
        // it is denied below 80 — see Shell_execute_is_denied_at_the_default_dial.)
        var decision = new CatalogDefaultToolLoopAutonomyGate(AutonomyDial.Max).Evaluate(toolName, "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed,
            $"'{toolName}' must be allowed at the max dial (100)");
    }

    [Test]
    public void Production_constructor_uses_the_shipped_dial_default_and_allows()
    {
        // The production constructor uses the shipped DEFAULT dial (70). A tool at
        // or below 70 (file_write = 25) is allowed.
        var decision = new CatalogDefaultToolLoopAutonomyGate().Evaluate("file_write", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
        decision.Dial.Should().Be(AcceptanceDefaults.DefaultAutonomyLevel);
        decision.MinAutonomy.Should().Be(25);
        decision.ActionKey.Should().Be(Tool(ToolAction.FileWrite));
        decision.Reason.Should().Be("at-or-above-min-autonomy");
    }

    [Test]
    public void Shell_execute_is_denied_at_the_default_dial()
    {
        // THE day-one behaviour change (Story 43-11 OQ1 / Amendment 2-D):
        // shell_execute sits at 80 (unbounded execution, holding the deployment's
        // secrets), above the shipped default dial of 70 — so every agent shell
        // call at the default dial now suspends for a person.
        var decision = new CatalogDefaultToolLoopAutonomyGate().Evaluate("shell_execute", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied);
        decision.Dial.Should().Be(AcceptanceDefaults.DefaultAutonomyLevel);
        decision.MinAutonomy.Should().Be(80);
        decision.ActionKey.Should().Be(Tool(ToolAction.ShellExecute));
        decision.Reason.Should().Be("below-min-autonomy");
    }

    // ── Story 42-10 (AC6, D7) — the shell secret-read reclassification ──────

    [TestCase("env")]
    [TestCase("printenv")]
    [TestCase("cat .env")]
    public void A_shell_command_that_reads_a_secret_reclassifies_to_secret_read(string command)
    {
        // At dial 85, shell_execute (80) automates but secret.read (90) does not,
        // so the reclassification is what flips the outcome — proving the grading
        // changed the action, not just the level.
        var gate = new CatalogDefaultToolLoopAutonomyGate(dial: 85);

        var decision = gate.Evaluate("shell_execute", $"{{\"command\":\"{command}\"}}");

        decision.ActionKey.Should().Be(new ActionKey(ActionNamespace.Effect, ExternalEffect.SecretRead.ToWire()),
            "a secret-reading shell command is graded as effect:secret.read, not tool:shell_execute");
        decision.MinAutonomy.Should().Be(90);
        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied, "secret.read (90) is above dial 85");
    }

    [Test]
    public void A_plain_shell_command_stays_tool_shell_execute()
    {
        var gate = new CatalogDefaultToolLoopAutonomyGate(dial: 85);

        var decision = gate.Evaluate("shell_execute", "{\"command\":\"ls -la\"}");

        decision.ActionKey.Should().Be(Tool(ToolAction.ShellExecute));
        decision.MinAutonomy.Should().Be(80);
        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed, "shell_execute (80) is at or below dial 85");
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
        //
        // The exemplar was `mcp__some_server__some_tool` until 2026-07-30. It is
        // now a genuinely unknown name, because MCP names are no longer
        // uncatalogued — see
        // An_mcp_tool_name_is_NOT_uncatalogued_it_resolves_to_the_mcp_effect below.
        // Epic D2 itself is unchanged and still pinned here.
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AutonomyDial.Min, minAutonomyOverride: _ => AutonomyDial.AlwaysHuman);

        var decision = gate.Evaluate("frobnicate_the_widget", "{}");

        decision.Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
        decision.Reason.Should().Be("uncatalogued");
        decision.ActionKey.Should().BeNull();
    }

    /// <summary>
    /// THE MCP governance decision (2026-07-30) at the one gate that enforces
    /// today.
    ///
    /// <para><b>Why this family is carved out of epic D2 while every other
    /// unknown name is not.</b> D2's bargain is "an unclassified action is allowed
    /// at RUNTIME because it is UNMERGEABLE IN CI". For every other capability the
    /// second half is real — the 43-8 harnesses sweep routes, executors,
    /// activities and background actors out of this tree. No harness can
    /// enumerate the tools of an MCP server: they live in another process, behind
    /// <c>POST /api/kb/mcp/tools/invoke</c>, and adding a server or a tool changes
    /// nothing in this repository. So for MCP the CI half never fires, the
    /// runtime tolerance is never paid for, and "allowed because it will be
    /// caught" would be false.</para>
    ///
    /// <para>Deliberately narrow: this changes the <c>mcp__*</c> family ONLY.
    /// Making all uncatalogued names deny would be a far larger behaviour change
    /// and is NOT what this decision does — the test above is its guard.</para>
    /// </summary>
    [TestCase("mcp__some_server__some_tool")]
    [TestCase("mcp__github__create_issue")]
    [TestCase("MCP__Shouty__Server")]
    public void An_mcp_tool_name_is_NOT_uncatalogued_it_resolves_to_the_mcp_effect(string name)
    {
        var gate = new CatalogDefaultToolLoopAutonomyGate(AutonomyDial.Min);

        var decision = gate.Evaluate(name, "{}");

        decision.ActionKey.Should().Be(
            new ActionKey(ActionNamespace.Effect, ExternalEffect.McpToolInvoke.ToWire()),
            "every mcp__server__tool name lands on the ONE coarse catalog member");
        decision.Reason.Should().NotBe("uncatalogued");
        decision.Outcome.Should().Be(ToolLoopGateOutcome.Denied,
            "effect:mcp.tool.invoke ships level 80 (unbounded execution); at dial 1 the "
            + "shipped default denies until an admin opts in or the dial reaches 80");
        decision.Reason.Should().Be("below-min-autonomy");
    }

    /// <summary>
    /// And it is a DEFAULT, not a hardcoded refusal: one action-scoped policy row
    /// at the floor re-opens the family. This is the reversibility the decision
    /// rests on — if it were not cheaply reversible, tightening a capability an
    /// operator may legitimately want would be the wrong call.
    /// </summary>
    [Test]
    public void An_admin_policy_row_re_opens_mcp()
    {
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AutonomyDial.Min, minAutonomyOverride: _ => AutonomyDial.Min);

        gate.Evaluate("mcp__some_server__some_tool", "{}")
            .Outcome.Should().Be(ToolLoopGateOutcome.Allowed);
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
        // gateable while git status stays automated. Dial = the default (70), above
        // git-read's level (5) so read stays automated; write is pinned AlwaysHuman.
        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AcceptanceDefaults.DefaultAutonomyLevel,
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
