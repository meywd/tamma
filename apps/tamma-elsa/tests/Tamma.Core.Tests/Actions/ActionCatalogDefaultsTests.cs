using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// The shipped catalog levels (Story 43-11, the zone model). Day one no longer
/// "reproduces today's uniform behaviour": every dial-governed action carries an
/// explicitly-chosen zone level in [1,100], so moving the dial changes the
/// automated set. The exhaustive (key → level) table and the strict-subset
/// property live in <see cref="ActionCatalogLevelTests"/>; this fixture pins the
/// invariants that survived the remap and the handful of individually-argued rows.
/// </summary>
[TestFixture]
public class ActionCatalogDefaultsTests
{
    [Test]
    public void NoShippedDescriptor_CarriesAlwaysHuman()
    {
        // Story 43-11 M6 / AC6: the four rows that used to ship AlwaysHuman
        // (design/sprint-plan/threat-model acceptances, mcp.tool.invoke) came onto
        // real levels. NO shipped descriptor carries the 101 sentinel any more —
        // "at 100 everything is automated" is true by construction.
        ActionCatalog.All
            .Where(d => d.DefaultMinAutonomy == AutonomyDial.AlwaysHuman)
            .Should().BeEmpty("no shipped descriptor may carry AlwaysHuman (43-11 M6)");

        // Every shipped value is a LEVEL, not merely a valid threshold. Machinery
        // rows sit at AutonomyDial.Min (inert — they never reach the dial).
        ActionCatalog.All.Should().OnlyContain(d => AutonomyDial.IsValidLevel(d.DefaultMinAutonomy),
            "the shipped value is a dial position, never the AlwaysHuman sentinel");
    }

    [Test]
    public void ShippedAcceptorFloor_IsTheCatalogLevelAgainstTheDial_ForEveryTypeAtEveryDial()
    {
        // Story 43-16 (form α): the acceptor floor is DERIVED, not a stored
        // constant — the ONE source of truth is the document-type's catalog level
        // against the dial. Pinned as a biconditional over every DocumentTypeKey ×
        // every valid dial position: the shipped floor is Human ⟺ the dial is
        // below that type's DefaultMinAutonomy. This is the lockstep guard that
        // replaces DesignDocumentType_MatchesAcceptanceDefaults: moving a
        // document-type level without the derivation (or vice versa) goes red.
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
        {
            var level = ActionCatalog
                .Get(new ActionKey(ActionNamespace.DocumentType, type.ToWire()))
                .DefaultMinAutonomy;

            foreach (var dial in AutonomyDial.ValidLevels())
            {
                var floor = AcceptanceFloors.ShippedFloorFor(type, dial);
                if (dial < level)
                    floor.Should().Be(AcceptorRequirement.Human,
                        $"'{type.ToWire()}' (level {level}) needs a person at dial {dial}");
                else
                    floor.Should().Be(AcceptorRequirement.Any,
                        $"'{type.ToWire()}' (level {level}) is orchestrator-approved at dial {dial}");
            }
        }
    }

    [Test]
    public void DeployAndRollback_ShipAtTheProductionZone()
    {
        // Was Deploy_ShipsAtMin_PerEpicDecisionD1 (deploy/rollback shipped at the
        // uniform Min). Under the zone model these carry the production/tenant
        // destruction levels. Story 43-12 split the coarse effect:deploy.promote-prod
        // into per-env keys: prod is the deploy-to-prod zone (90) and lands on
        // effect:deploy.prod; rollback is the delete/rollback zone (95). The existing
        // business-mode gate (DeploymentPipelineWorkflow) is untouched; 43-9 joins by OR.
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.prod")).DefaultMinAutonomy.Should().Be(90);
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.rollback")).DefaultMinAutonomy.Should().Be(95);
        ActionCatalog.Get(ActionKey.Parse("agent-action:deploy")).DefaultMinAutonomy.Should().Be(90);
        ActionCatalog.Get(ActionKey.Parse("agent-action:rollback")).DefaultMinAutonomy.Should().Be(95);
    }

    [Test]
    public void PerTargetMergeAndDeployKeys_ShipAtTheirZoneLevels()
    {
        // Story 43-12 — the per-target merge/deploy zone-ladder keys. Merge splits by
        // PR base branch (dev 55 / qa 60 / main 65); deploy splits by target env
        // (dev 70 / qa 75 / uat 80 / staging 85 / prod 90). Plus the two reserved
        // source-control-write keys: git.checks.bypass (50) and git.webhook.register (85).
        ActionCatalog.Get(ActionKey.Parse("effect:git.merge.dev")).DefaultMinAutonomy.Should().Be(55);
        ActionCatalog.Get(ActionKey.Parse("effect:git.merge.qa")).DefaultMinAutonomy.Should().Be(60);
        ActionCatalog.Get(ActionKey.Parse("effect:git.merge.main")).DefaultMinAutonomy.Should().Be(65);
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.dev")).DefaultMinAutonomy.Should().Be(70);
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.qa")).DefaultMinAutonomy.Should().Be(75);
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.uat")).DefaultMinAutonomy.Should().Be(80);
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.staging")).DefaultMinAutonomy.Should().Be(85);
        ActionCatalog.Get(ActionKey.Parse("effect:deploy.prod")).DefaultMinAutonomy.Should().Be(90);
        ActionCatalog.Get(ActionKey.Parse("effect:git.checks.bypass")).DefaultMinAutonomy.Should().Be(50);
        ActionCatalog.Get(ActionKey.Parse("effect:git.webhook.register")).DefaultMinAutonomy.Should().Be(85);
    }

    [Test]
    public void McpToolInvoke_ShipsAtTheUnboundedExecutionZone()
    {
        // Was McpToolInvoke_ShipsAlwaysHuman_BecauseTheCiHalfCannotExist. The
        // 2026-07-30 MCP governance decision SURVIVES IN SUBSTANCE — mcp still
        // needs a person at every dial position a deployment ships with (default
        // 70 < 80) — but it is no longer UNCONDITIONAL: at dial ≥ 80 it automates,
        // which is what "at 100 everything is automated" requires. I2 · Command:
        // unbounded reach outside the deployment.
        ActionCatalog.Get(ActionKey.Parse("effect:mcp.tool.invoke"))
            .DefaultMinAutonomy.Should().Be(80);

        // Still a DEFAULT, not a refusal: an admin policy row re-opens it.
        ActionCatalog.Get(ActionKey.Parse("effect:mcp.tool.invoke"))
            .Enforceable.Should().BeTrue();
        AutonomyDial.IsValidLevel(
            ActionCatalog.Get(ActionKey.Parse("effect:mcp.tool.invoke")).DefaultMinAutonomy)
            .Should().BeTrue("a default the admin API would reject is a rule with no off switch");
    }

    [Test]
    public void TriageIntake_ShipsAtTheReadOnlyZone_FloorComesFromAlwaysEscalate()
    {
        // 43-3 D7 (unchanged in substance, only the catalog number moved):
        // TriageBindingHelper ships a live EscalationClass(AgentAction,
        // TriageIntake) — 43-5's evaluator contributes AlwaysHuman as a max() FLOOR
        // from that legacy surface, so triage-intake's EFFECTIVE threshold does not
        // move at all. Duplicating the floor as a catalog level would make deleting
        // the legacy entry fail to lower the threshold. The catalog level is the
        // read-only zone (5); the composed outcome is pinned by 43-5's
        // ShippedTriageDefault_StillEscalates.
        ActionCatalog.Get(ActionKey.Parse("agent-action:triage-intake"))
            .DefaultMinAutonomy.Should().Be(5);
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
        // The 101 sentinel is NOT deleted (Story 43-11 M6): it survives for three
        // live jobs — this UnclassifiedFallback, the fail-closed unreadable-policy
        // substitution, and the legacy always-escalate floor. What ended is its use
        // as a shipped descriptor default (NoShippedDescriptor_CarriesAlwaysHuman).
        ActionCatalog.UnclassifiedFallback.Should().Be(AutonomyDial.AlwaysHuman);
    }
}
