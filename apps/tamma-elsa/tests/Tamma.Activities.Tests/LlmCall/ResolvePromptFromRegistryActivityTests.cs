using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Api.Services.Agents;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 27-18 — boundary taxonomy validation for
/// <see cref="ResolvePromptFromRegistryActivity"/>. The activity body inlines
/// the Elsa <c>ActivityExecutionContext</c> interaction (which can't be cheaply
/// mocked — see <c>CheckBudgetActivityEmissionTests</c>), so we exercise the
/// extracted static <see cref="ResolvePromptFromRegistryActivity.ValidateTaxonomy"/>
/// boundary directly. This proves the fail-fast contract: an invalid
/// <c>(role, action)</c> throws rather than degrading to a plain fallback.
/// </summary>
[TestFixture]
public class ResolvePromptFromRegistryActivityTests
{
    [Test]
    public void ValidateTaxonomy_ValidPair_DoesNotThrow()
    {
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            AgentRole.Developer.ToWire(), AgentAction.ImplementFeature.ToWire());

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateTaxonomy_ValidPair_AcceptsSharedToken()
    {
        // context-scan is shared across roles; senior_developer owns it.
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "senior_developer", "context-scan");

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateTaxonomy_UnknownRole_Throws()
    {
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "not-a-role", "implement-feature");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_UnknownAction_Throws()
    {
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "developer", "not-a-real-action");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_DeadLegacyGenericAction_Throws()
    {
        // 'implement' was the old flat-vocabulary generic action; it is no
        // longer a taxonomy token (Story 27-15/27-18) → fail-fast, never a
        // silent mismatch or plain fallback.
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "developer", "implement");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_LegacyRoleAlias_IsAccepted()
    {
        // RolePhaseMap normalises legacy TS role aliases (implementer→developer)
        // before parsing, so a suspended workflow emitting a legacy role still
        // validates against a taxonomy-valid action.
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "implementer", "implement-feature");

        act.Should().NotThrow();
    }
}
