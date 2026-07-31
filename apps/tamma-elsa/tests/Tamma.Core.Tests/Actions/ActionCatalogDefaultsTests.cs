using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// The behaviour-preserving shipped defaults (Story 43-3 AC7–AC9, epic decision
/// D1: v1 ENFORCES, so day one must reproduce today's behaviour EXACTLY).
///
/// <para>
/// Derivation rule (43-3 D4): a member ships <c>AlwaysHuman</c> if and only if,
/// TODAY, a person must act before it can complete. Applying it yields a
/// ONE-member set — smaller than design.md §3.1's "~15", and the honest answer:
/// Tamma gates almost nothing today, and a catalog that claims otherwise on day
/// one is a catalog that changed behaviour while claiming not to.
/// </para>
/// </summary>
[TestFixture]
public class ActionCatalogDefaultsTests
{
    /// <summary>
    /// THE explicit AlwaysHuman table. Growing it later is a reviewed decision —
    /// add a line here AND in the descriptor, with the evidence that a person
    /// must act today.
    /// </summary>
    private static readonly string[] ShippedAlwaysHuman =
    {
        // AcceptanceDefaults.For(Design) ships AcceptorRequirement.Human — at
        // 43-3 time the ONLY production occurrence (AcceptanceDefaults.cs; 43-3
        // C2). design.md §3.1's "the 10 document-types with a human acceptor" is
        // VERIFIED FALSE: Plan/Review get panel SELECTION (a reviewer roster),
        // not a human acceptor. Under enforcing-v1 that error would have gated
        // nine document types on day one.
        "document-type:design",
        // Story 41-1b D1 grew the AcceptorRequirement.Human set by two — the
        // catalog default follows the real AcceptanceDefaults switch (43-3 D4):
        // a sprint commitment and an unmitigated-high-risk escalation call are
        // human decisions from the day the types exist, so this is not a
        // behaviour change on an existing surface.
        "document-type:sprint-plan",
        "document-type:threat-model",
        // 3 → 4 (2026-07-30, the MCP governance decision). This one is NOT
        // derived from "a person must act today" — it is the deliberate,
        // reviewed exception to 43-3 D4, and the reasoning is different in kind:
        //
        //   Epic D2 tolerates an UNCLASSIFIED action at runtime because the drift
        //   harnesses make it unmergeable in CI. MCP is the one capability family
        //   for which that second half CANNOT EXIST — an MCP server's tool list
        //   lives in another process and no harness in this tree can enumerate
        //   it, so adding a server, or a tool on a server, never becomes
        //   unmergeable. Shipping it at Min meant an open capability class that
        //   was simultaneously unenforceable and drift-invisible.
        //
        // The behaviour-preserving obligation is still met, which is why this is
        // a default rather than a hard refusal: NO MCP EXECUTOR IS REGISTERED, so
        // no agent path can invoke MCP today (ToolExecutorRegistry holds
        // file_read/file_write/search_code/shell_execute/git_operations/
        // run_tests). The one live MCP surface, POST /api/kb/mcp/tools/invoke, is
        // a human-authenticated SettingsManage route. Nothing that worked
        // yesterday stops working.
        //
        // Reversal is ONE admin policy row (action scope, min = AutonomyDial.Min)
        // — deliberately cheap, because the right long-term answer is per-server /
        // per-tool catalog members plus a sweep, and this default should be
        // revisited the moment those exist (epic open question 1).
        "effect:mcp.tool.invoke",
    };

    [Test]
    public void ShippedDefaults_ReproduceTodaysGatingBehaviour()
    {
        var alwaysHuman = ActionCatalog.All
            .Where(d => d.DefaultMinAutonomy == AutonomyDial.AlwaysHuman)
            .Select(d => d.Key.ToWire());

        alwaysHuman.Should().BeEquivalentTo(ShippedAlwaysHuman,
            "the AlwaysHuman set is derived (a person must act TODAY), small, and explicit — 43-3 D4");
    }

    [Test]
    public void EveryOtherMember_DefaultsToMin()
    {
        // The complement: a member added later lands as automated-at-the-floor and
        // the choice is visible in the diff, never implicit.
        var others = ActionCatalog.All.Where(d => !ShippedAlwaysHuman.Contains(d.Key.ToWire()));

        others.Should().OnlyContain(d => d.DefaultMinAutonomy == AutonomyDial.Min);
    }

    [Test]
    public void DesignDocumentType_MatchesAcceptanceDefaults()
    {
        // Reads the REAL shipped switch so the two surfaces cannot diverge (43-3
        // AC8/C2): design's default is AlwaysHuman BECAUSE AcceptanceDefaults
        // pins its acceptor to a human; every other type is Any and ships Min.
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
        {
            var descriptor = ActionCatalog.Get(new ActionKey(ActionNamespace.DocumentType, type.ToWire()));
            var acceptor = AcceptanceDefaults.For(type).AcceptorRequirement;

            if (acceptor == AcceptorRequirement.Human)
            {
                type.Should().BeOneOf(new[]
                    {
                        DocumentTypeKey.Design, DocumentTypeKey.SprintPlan, DocumentTypeKey.ThreatModel,
                    },
                    "AcceptorRequirement.Human occurs for exactly design (39-13 D4) plus " +
                    "sprint-plan/threat-model (Story 41-1b D1)");
                descriptor.DefaultMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
            }
            else
            {
                descriptor.DefaultMinAutonomy.Should().Be(AutonomyDial.Min,
                    $"'{type.ToWire()}' has no human acceptor today");
            }
        }
    }

    [Test]
    public void Deploy_ShipsAtMin_PerEpicDecisionD1()
    {
        // BINDING deviation from design.md §3.1 (43-3 D3/C4): the design proposed
        // AlwaysHuman for these, reasoning "enforce defaults false so nothing
        // changes". Epic decision D1 removed that shield — v1 ENFORCES — so
        // AlwaysHuman here would gate every production deploy on upgrade day. The
        // admin opts in. Restoring AlwaysHuman is NOT a bug fix; it requires
        // deleting this assertion and its reasoning.
        // Safety is not weakened: DeploymentPipelineWorkflow's business-mode gate
        // (:243 → WaitForDeploymentApprovalActivity) is untouched, and 43-9
        // adopts the autonomy gate by OR, never by replacement.
        //
        // RENAMED 2026-07-30 (was DeployAndMcp_ShipAtMin_PerEpicDecisionD1):
        // effect:mcp.tool.invoke left this set — the deploy half of the D1
        // argument still stands, the MCP half does not. See
        // McpToolInvoke_ShipsAlwaysHuman_BecauseTheCiHalfCannotExist.
        foreach (var wire in new[] { "effect:deploy.promote-prod", "effect:deploy.rollback" })
            ActionCatalog.Get(ActionKey.Parse(wire)).DefaultMinAutonomy.Should().Be(AutonomyDial.Min, wire);
    }

    /// <summary>
    /// The MCP governance decision (2026-07-30). This is the deliberate exception
    /// to the deploy rule directly above, and the two must be read together:
    /// deploy ships at Min because D2's safety net (unclassified at runtime,
    /// unmergeable in CI) genuinely covers it — a new deploy route is visible to
    /// the 43-8 harnesses. MCP ships AlwaysHuman because that net has a hole
    /// exactly its shape: no harness can enumerate a remote MCP server's tools,
    /// so an MCP capability NEVER becomes unmergeable and the runtime tolerance
    /// is unpaid-for.
    /// </summary>
    [Test]
    public void McpToolInvoke_ShipsAlwaysHuman_BecauseTheCiHalfCannotExist()
    {
        ActionCatalog.Get(ActionKey.Parse("effect:mcp.tool.invoke"))
            .DefaultMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);

        // Still a DEFAULT, not a refusal: an admin policy row re-opens it, which
        // is what makes this a tightening rather than a removal of a capability.
        ActionCatalog.Get(ActionKey.Parse("effect:mcp.tool.invoke"))
            .Enforceable.Should().BeTrue();
        AutonomyDial.IsValidThreshold(
            ActionCatalog.Get(ActionKey.Parse("effect:mcp.tool.invoke")).DefaultMinAutonomy)
            .Should().BeTrue("a default the admin API would reject is a rule with no off switch");
    }

    [Test]
    public void TriageIntake_ShipsAtMin_FloorComesFromAlwaysEscalate()
    {
        // 43-3 D7: TriageBindingHelper ships a live EscalationClass(AgentAction,
        // TriageIntake) — 43-5's evaluator contributes AlwaysHuman as a max()
        // FLOOR from that legacy surface. Duplicating it as a catalog default
        // would make deleting the legacy entry fail to lower the threshold. The
        // composed outcome is pinned by 43-5's ShippedTriageDefault_StillEscalates.
        ActionCatalog.Get(ActionKey.Parse("agent-action:triage-intake"))
            .DefaultMinAutonomy.Should().Be(AutonomyDial.Min);
    }

    [Test]
    public void EveryDefault_IsOverridableOverTheApi()
    {
        // Story 43-2 AC15 (via 39-23 AC2): every gating rule must be replaceable
        // over the API — a shipped default the API would reject is a rule with no
        // off switch.
        ActionCatalog.All.Should().OnlyContain(d => AutonomyDial.IsValidThreshold(d.DefaultMinAutonomy));
    }

    [Test]
    public void UnclassifiedFallback_is_AlwaysHuman()
    {
        ActionCatalog.UnclassifiedFallback.Should().Be(AutonomyDial.AlwaysHuman);
    }
}
