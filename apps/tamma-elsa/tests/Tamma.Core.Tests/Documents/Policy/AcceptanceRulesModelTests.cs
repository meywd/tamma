using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Model + validation drift tests for <see cref="AcceptanceRules"/> and its closed
/// enums (Story 39-5 AC1, AC4 validation half, AC8 bounds-rejection clause).
/// Validation REJECTS, never clamps.
/// </summary>
[TestFixture]
public class AcceptanceRulesModelTests
{
    [Test]
    public void Valid_record_passes_validation() =>
        AcceptanceTestData.ValidRules().Invoking(r => r.Validate()).Should().NotThrow();

    // ── Autonomy 70–100 ──
    [TestCase(69, false)]
    [TestCase(70, true)]
    [TestCase(100, true)]
    [TestCase(101, false)]
    public void AutonomyLevel_is_bounded_70_to_100(int level, bool ok) =>
        Assert(AcceptanceTestData.ValidRules() with { AutonomyLevel = level }, ok);

    // ── Rounds 1–10 ──
    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(10, true)]
    [TestCase(11, false)]
    public void MaxRevisionRounds_is_bounded_1_to_10(int rounds, bool ok) =>
        Assert(AcceptanceTestData.ValidRules() with { MaxRevisionRounds = rounds }, ok);

    // ── Repair 0–10 ──
    [TestCase(-1, false)]
    [TestCase(0, true)]
    [TestCase(10, true)]
    [TestCase(11, false)]
    public void MaxValidationRepairAttempts_is_bounded_0_to_10(int repair, bool ok) =>
        Assert(AcceptanceTestData.ValidRules() with { MaxValidationRepairAttempts = repair }, ok);

    // ── Threshold [0,1] ──
    [TestCase(-0.01, false)]
    [TestCase(0.0, true)]
    [TestCase(1.0, true)]
    [TestCase(1.1, false)]
    public void AmbiguityEscalationThreshold_is_bounded_0_to_1(double t, bool ok) =>
        Assert(AcceptanceTestData.ValidRules() with { AmbiguityEscalationThreshold = t }, ok);

    [Test]
    public void Unknown_always_escalate_document_type_key_rejects()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            AlwaysEscalate = new[] { new EscalationClass(EscalationClassKind.DocumentType, "not-a-type") },
        };
        rules.Invoking(r => r.Validate()).Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACCEPTANCE_RULES.INVALID");
    }

    [Test]
    public void Unknown_always_escalate_agent_action_key_rejects()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            AlwaysEscalate = new[] { new EscalationClass(EscalationClassKind.AgentAction, "not-an-action") },
        };
        rules.Invoking(r => r.Validate()).Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACCEPTANCE_RULES.INVALID");
    }

    [Test]
    public void Valid_always_escalate_classes_pass()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            AlwaysEscalate = new[]
            {
                new EscalationClass(EscalationClassKind.DocumentType, DocumentTypeKey.Design.ToWire()),
                new EscalationClass(EscalationClassKind.AgentAction, AgentAction.WriteAdr.ToWire()),
            },
        };
        rules.Invoking(r => r.Validate()).Should().NotThrow();
    }

    [Test]
    public void Unknown_reviewer_role_rejects()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            ReviewerSelection = AcceptanceTestData.SingleArchitect() with { ReviewerRole = "not-a-role" },
        };
        rules.Invoking(r => r.Validate()).Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACCEPTANCE_RULES.INVALID");
    }

    [Test]
    public void SingleReviewer_without_role_rejects()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            ReviewerSelection = AcceptanceTestData.SingleArchitect() with { ReviewerRole = null },
        };
        rules.Invoking(r => r.Validate()).Should().Throw<TammaError>();
    }

    [Test]
    public void Panel_with_empty_roster_rejects()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            ReviewerSelection = new ReviewerSelection(
                ReviewerMode.Panel, null, Array.Empty<string>(), null, ReviewDecisionRule.Majority),
        };
        rules.Invoking(r => r.Validate()).Should().Throw<TammaError>();
    }

    [Test]
    public void Panel_quorum_beyond_roster_rejects()
    {
        var rules = AcceptanceTestData.ValidRules() with
        {
            ReviewerSelection = new ReviewerSelection(
                ReviewerMode.Panel, null, new[] { AgentRole.Architect.ToWire() }, 2, ReviewDecisionRule.Majority),
        };
        rules.Invoking(r => r.Validate()).Should().Throw<TammaError>();
    }

    // ── Story 39-13 D4 — acceptor requirement (per-type autonomy floor) ──

    [Test]
    public void AcceptorRequirement_defaults_to_any_when_unspecified()
    {
        // The field is ADDITIVE: a record built without it must behave exactly as it did
        // before the field existed, so `any` has to be the CLR default (first member).
        var rules = new AcceptanceRules
        {
            AutonomyLevel = 70,
            MaxRevisionRounds = 2,
            MaxValidationRepairAttempts = 2,
            AmbiguityEscalationThreshold = 0.7,
            AlwaysEscalate = Array.Empty<EscalationClass>(),
            ReviewerSelection = AcceptanceTestData.SingleArchitect(),
            DecisionGuidance = "d",
            RoutingGuidance = "r",
        };

        rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
        default(AcceptorRequirement).Should().Be(AcceptorRequirement.Any);
        rules.Invoking(r => r.Validate()).Should().NotThrow();
    }

    [TestCase(AcceptorRequirement.Any)]
    [TestCase(AcceptorRequirement.Human)]
    public void Known_acceptor_requirements_pass_validation(AcceptorRequirement requirement) =>
        Assert(AcceptanceTestData.ValidRules() with { AcceptorRequirement = requirement }, shouldPass: true);

    [Test]
    public void Unknown_acceptor_requirement_rejects() =>
        Assert(AcceptanceTestData.ValidRules() with { AcceptorRequirement = (AcceptorRequirement)99 }, shouldPass: false);

    [Test]
    public void AcceptorRequirement_roundtrips_through_the_canonical_serializer()
    {
        var rules = AcceptanceTestData.ValidRules() with { AcceptorRequirement = AcceptorRequirement.Human };

        var json = AcceptanceRulesJson.Serialize(rules);
        json.Should().Contain("\"acceptorRequirement\":\"human\"", "the wire spelling is the [Wire] token");

        AcceptanceRulesJson.Deserialize(json).AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
        AcceptanceRulesJson.Deserialize(AcceptanceRulesJson.Serialize(AcceptanceTestData.ValidRules()))
            .AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
    }

    [Test]
    public void A_rules_row_written_before_the_field_existed_deserializes_to_any()
    {
        // Backward compatibility: a persisted `acceptance_rules_overrides.rules_json` blob
        // from before 39-13 carries no `acceptorRequirement` key. It must load — and load
        // as `any`, i.e. exactly the behavior it had when it was written.
        const string legacyJson =
            "{\"autonomyLevel\":70,\"maxRevisionRounds\":2,\"maxValidationRepairAttempts\":2," +
            "\"ambiguityEscalationThreshold\":0.7,\"alwaysEscalate\":[]," +
            "\"reviewerSelection\":{\"mode\":\"single-reviewer\",\"reviewerRole\":\"architect\"," +
            "\"panelRoles\":[],\"quorum\":null,\"decisionRule\":\"unanimous\"}," +
            "\"decisionGuidance\":\"d\",\"routingGuidance\":\"r\"}";

        var rules = AcceptanceRulesJson.Deserialize(legacyJson);
        rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
        rules.AutonomyLevel.Should().Be(70);
    }

    // ── Enum wire round-trips + count pins ──

    [Test]
    public void AcceptorRequirement_has_two_members_with_wire_roundtrip()
    {
        Enum.GetValues<AcceptorRequirement>().Length.Should().Be(2);
        AcceptorRequirement.Any.ToWire().Should().Be("any");
        AcceptorRequirement.Human.ToWire().Should().Be("human");
    }


    [Test]
    public void EscalationClassKind_has_two_members_with_wire_roundtrip()
    {
        Enum.GetValues<EscalationClassKind>().Length.Should().Be(2);
        EscalationClassKind.DocumentType.ToWire().Should().Be("document-type");
        EscalationClassKind.AgentAction.ToWire().Should().Be("agent-action");
    }

    [Test]
    public void ReviewerMode_wire_roundtrip()
    {
        ReviewerMode.SingleReviewer.ToWire().Should().Be("single-reviewer");
        ReviewerMode.Panel.ToWire().Should().Be("panel");
    }

    [Test]
    public void ReviewDecisionRule_wire_roundtrip()
    {
        ReviewDecisionRule.Unanimous.ToWire().Should().Be("unanimous");
        ReviewDecisionRule.Majority.ToWire().Should().Be("majority");
    }

    [Test]
    public void AcceptanceRulesSource_has_three_members_with_wire_roundtrip()
    {
        Enum.GetValues<AcceptanceRulesSource>().Length.Should().Be(3);
        AcceptanceRulesSource.SystemDefault.ToWire().Should().Be("system-default");
        AcceptanceRulesSource.PrincipalDefault.ToWire().Should().Be("principal-default");
        AcceptanceRulesSource.TypeOverride.ToWire().Should().Be("type-override");
    }

    [Test]
    public void AcceptanceEscalationReason_has_exactly_six_members() =>
        Enum.GetValues<AcceptanceEscalationReason>().Length.Should().Be(6);

    [TestCase(AcceptanceEscalationReason.RoundsExhausted, "rounds-exhausted")]
    [TestCase(AcceptanceEscalationReason.AlwaysEscalateClass, "always-escalate-class")]
    [TestCase(AcceptanceEscalationReason.BlockingReviewViolation, "blocking-review-violation")]
    [TestCase(AcceptanceEscalationReason.AmbiguityAboveThreshold, "ambiguity-above-threshold")]
    [TestCase(AcceptanceEscalationReason.AcceptorJudgment, "acceptor-judgment")]
    [TestCase(AcceptanceEscalationReason.RejectRequiresHuman, "reject-requires-human")]
    public void AcceptanceEscalationReason_wire_roundtrip(AcceptanceEscalationReason r, string wire) =>
        r.ToWire().Should().Be(wire);

    [Test]
    public void ToLifecycleOutcome_maps_two_reasons_and_nulls_the_other_four()
    {
        AcceptanceEscalationReason.RoundsExhausted.ToLifecycleOutcome()
            .Should().Be(DocumentLifecycleOutcome.RoundsExhausted);
        AcceptanceEscalationReason.AmbiguityAboveThreshold.ToLifecycleOutcome()
            .Should().Be(DocumentLifecycleOutcome.AmbiguityAboveThreshold);

        AcceptanceEscalationReason.AlwaysEscalateClass.ToLifecycleOutcome().Should().BeNull();
        AcceptanceEscalationReason.BlockingReviewViolation.ToLifecycleOutcome().Should().BeNull();
        AcceptanceEscalationReason.AcceptorJudgment.ToLifecycleOutcome().Should().BeNull();
        AcceptanceEscalationReason.RejectRequiresHuman.ToLifecycleOutcome().Should().BeNull();
    }

    private static void Assert(AcceptanceRules rules, bool shouldPass)
    {
        if (shouldPass)
            rules.Invoking(r => r.Validate()).Should().NotThrow();
        else
            rules.Invoking(r => r.Validate()).Should().Throw<TammaError>()
                .Which.Code.Should().Be("ACCEPTANCE_RULES.INVALID");
    }
}
