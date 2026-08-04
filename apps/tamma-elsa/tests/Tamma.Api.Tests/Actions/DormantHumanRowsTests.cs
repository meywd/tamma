using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-13 AC7 — <b>the 7 dormant HUMAN rows, pinned dormant.</b> Per the
/// 43-11 caller-kind re-audit these keys are performed by PEOPLE today (admin
/// dashboard routes); their catalog levels bind ONLY if an LLM path ever
/// reaches those routes (e.g. the shell-curl bypass, which the gate should then
/// catch). Two pins:
///
/// <list type="number">
/// <item>a human caller passes each at dial Min — the predicate's promise;</item>
/// <item>none of the seven has a <c>method:</c> (mediation-client) enforcement
/// site — the INTENDED failure: adding a <c>TammaApiClient</c>
/// <c>[PerformsEffect]</c> method (an LLM path) for one of these keys turns
/// this red until the dormancy fixture is consciously revisited.</item>
/// </list>
/// </summary>
[TestFixture]
public class DormantHumanRowsTests
{
    /// <summary>Exactly the 43-11 re-audit's Level-20 HUMAN table. Do not edit
    /// without a 43-11 amendment.</summary>
    private static readonly string[] DormantHumanRows =
    {
        "effect:schedule.create",
        "effect:schedule.update",
        "effect:schedule.delete",
        "effect:mentorship.session.start",
        "effect:mentorship.session.pause",
        "effect:mentorship.session.resume",
        "effect:mentorship.session.cancel",
    };

    [Test]
    public void TheSevenDormantHumanRows_PassForAHumanAtDialMin()
    {
        var user = GovernancePrincipal.ForUser(Guid.NewGuid());
        var baseRules = new ResolvedAcceptanceRules(
            AcceptanceDefaults.Rules with { AutonomyLevel = AutonomyDial.Min },
            AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);

        DormantHumanRows.Should().HaveCount(7).And.OnlyHaveUniqueItems();

        foreach (var wire in DormantHumanRows)
        {
            var key = ActionKey.Parse(wire);
            ActionCatalog.ByKey.Should().ContainKey(key, "the fixture names catalog rows");
            ActionCatalog.Get(key).IsMachinery.Should().BeFalse(
                $"'{wire}' is a HUMAN row, not machinery — it keeps its level for the "
                + "day an LLM path reaches the route");

            var decision = AutonomyGateEvaluator.Evaluate(
                new AutonomyQuery(key, user, Caller: CallerKind.Human),
                GovernancePolicySnapshot.Empty, baseRules);

            decision.Outcome.Should().Be(AutonomyOutcome.Automated,
                $"a person on '{wire}' is never gated, whatever the level says");
            decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonCallerHuman, wire);
        }
    }

    [Test]
    public void NoDormantHumanRow_HasAMediationMethodSite()
    {
        // The dormancy pin. `method:` sites are TammaApiClient [PerformsEffect]
        // methods — the engine's (LLM-path) way to an effect. The mentorship
        // rows keep exactly their controller `route:` site; the schedule rows
        // have no site at all (deliberately unbound — KnownUngovernedEndpoints,
        // 2026-08-01).
        var sites = GovernanceHostFixture.Services.GetRequiredService<IActionEnforcementSites>();

        foreach (var wire in DormantHumanRows)
        {
            var key = ActionKey.Parse(wire);
            var rowSites = sites.For(key);

            rowSites.Should().NotContain(
                s => s.StartsWith(ActionEnforcementSites.MethodPrefix, StringComparison.Ordinal),
                $"'{wire}' is dormant-HUMAN: a mediation-client method site is an LLM path, "
                + "and adding one must be a conscious revisit of this fixture (AC7's "
                + "intended failure)");

            if (wire.StartsWith("effect:mentorship.", StringComparison.Ordinal))
            {
                rowSites.Should().ContainSingle(
                    s => s.StartsWith(ActionEnforcementSites.RoutePrefix, StringComparison.Ordinal),
                    $"'{wire}' is bound at its controller route (43-8's [Governs] shape)");
            }
            else
            {
                rowSites.Should().BeEmpty(
                    $"'{wire}' is deliberately unbound today (KnownUngovernedEndpoints); a "
                    + "site appearing here means a binding landed — re-read AC7 before "
                    + "editing this fixture");
            }
        }
    }
}
