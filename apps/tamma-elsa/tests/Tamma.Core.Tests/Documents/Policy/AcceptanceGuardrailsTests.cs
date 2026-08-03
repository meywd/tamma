using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Guardrail tests (Story 39-5 AC8). Pre-gate short-circuits, the forged-approval
/// clamp, the human-only reject clamp, the round-budget clamp, and a hand-rolled
/// seeded-random property test (Design Decision D11 — NO FsCheck) proving bounded
/// termination.
/// </summary>
[TestFixture]
public class AcceptanceGuardrailsTests
{
    private static AcceptanceGateContext Ctx(
        AcceptanceRules rules,
        ReviewDecision decision = ReviewDecision.Approve,
        bool blocking = false,
        int roundsUsed = 0,
        ApprovalChannel channel = ApprovalChannel.Orchestrator,
        string? actionWire = null,
        DocumentTypeKey type = DocumentTypeKey.Plan) =>
        new(type, actionWire, new ReviewFacts(decision, blocking), roundsUsed, rules, channel);

    // ── Pre-gate ──

    [Test]
    public void PreGate_escalates_on_always_escalate_document_type_class()
    {
        var rules = AcceptanceDefaults.Rules with
        {
            AlwaysEscalate = new[] { new EscalationClass(EscalationClassKind.DocumentType, DocumentTypeKey.Plan.ToWire()) },
        };
        AcceptanceGuardrails.TryPreGate(Ctx(rules), out var esc).Should().BeTrue();
        esc.Reason.Should().Be(AcceptanceEscalationReason.AlwaysEscalateClass);
    }

    [Test]
    public void PreGate_escalates_on_always_escalate_agent_action_class()
    {
        var action = AgentAction.WriteAdr.ToWire();
        var rules = AcceptanceDefaults.Rules with
        {
            AlwaysEscalate = new[] { new EscalationClass(EscalationClassKind.AgentAction, action) },
        };
        AcceptanceGuardrails.TryPreGate(Ctx(rules, actionWire: action), out var esc).Should().BeTrue();
        esc.Reason.Should().Be(AcceptanceEscalationReason.AlwaysEscalateClass);
    }

    [Test]
    public void PreGate_escalates_when_rounds_exhausted()
    {
        var rules = AcceptanceDefaults.Rules with { MaxRevisionRounds = 2 };
        AcceptanceGuardrails.TryPreGate(Ctx(rules, roundsUsed: 2), out var esc).Should().BeTrue();
        esc.Reason.Should().Be(AcceptanceEscalationReason.RoundsExhausted);
    }

    [Test]
    public void PreGate_passes_when_no_class_matches_and_rounds_remain()
    {
        var rules = AcceptanceDefaults.Rules with { MaxRevisionRounds = 3 };
        AcceptanceGuardrails.TryPreGate(Ctx(rules, roundsUsed: 1), out _).Should().BeFalse();
    }

    // ── Clamp: forged approval ──

    [Test]
    public void Clamp_Accept_with_blocking_issue_escalates()
    {
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.Accept(),
            Ctx(AcceptanceDefaults.Rules, ReviewDecision.Approve, blocking: true));
        result.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.BlockingReviewViolation);
    }

    [Test]
    public void Clamp_Accept_over_request_changes_escalates()
    {
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.Accept(),
            Ctx(AcceptanceDefaults.Rules, ReviewDecision.RequestChanges, blocking: false));
        result.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.BlockingReviewViolation);
    }

    [Test]
    public void Clamp_Accept_over_clean_approval_passes_through()
    {
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.Accept(),
            Ctx(AcceptanceDefaults.Rules, ReviewDecision.Approve, blocking: false));
        result.Should().BeOfType<AcceptanceDecision.Accept>();
    }

    // ── Clamp: reject is human-only ──

    [Test]
    public void Clamp_Reject_on_orchestrator_channel_escalates()
    {
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.Reject("no"),
            Ctx(AcceptanceDefaults.Rules, channel: ApprovalChannel.Orchestrator));
        result.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.RejectRequiresHuman);
    }

    [TestCase(ApprovalChannel.User)]
    [TestCase(ApprovalChannel.Api)]
    public void Clamp_Reject_on_human_channel_passes_through(ApprovalChannel channel)
    {
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.Reject("final no"),
            Ctx(AcceptanceDefaults.Rules, channel: channel));
        result.Should().BeOfType<AcceptanceDecision.Reject>();
    }

    // ── Clamp: round budget ──

    [Test]
    public void Clamp_RequestRevision_past_budget_escalates()
    {
        var rules = AcceptanceDefaults.Rules with { MaxRevisionRounds = 2 };
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.RequestRevision("again"),
            Ctx(rules, roundsUsed: 2));
        result.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.RoundsExhausted);
    }

    [Test]
    public void Clamp_RequestRevision_within_budget_passes_through()
    {
        var rules = AcceptanceDefaults.Rules with { MaxRevisionRounds = 3 };
        var result = AcceptanceGuardrails.Clamp(
            new AcceptanceDecision.RequestRevision("again"),
            Ctx(rules, roundsUsed: 1));
        result.Should().BeOfType<AcceptanceDecision.RequestRevision>();
    }

    [Test]
    public void Clamp_never_manufactures_Accept_from_a_non_Accept_input()
    {
        var rules = AcceptanceDefaults.Rules;
        AcceptanceDecision[] nonAccepts =
        {
            new AcceptanceDecision.RequestRevision("x"),
            new AcceptanceDecision.Reject("x"),
            new AcceptanceDecision.Escalate(AcceptanceEscalationReason.AcceptorJudgment, "x"),
        };
        foreach (var d in nonAccepts)
            AcceptanceGuardrails.Clamp(d, Ctx(rules)).Should().NotBeOfType<AcceptanceDecision.Accept>();
    }

    // ── Property-style: bounded termination (D11) ──

    [Test]
    public void Property_arbitrary_decision_sequences_terminate_within_bounds()
    {
        const int iterations = 1000;
        for (var i = 0; i < iterations; i++)
        {
            var seed = i;
            var rng = new Random(seed);
            try
            {
                RunOneTerminationTrial(rng);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Termination trial failed for seed {seed}: {ex.Message}");
            }
        }
    }

    private static void RunOneTerminationTrial(Random rng)
    {
        // Random VALID rules (empty always-escalate so we exercise the round
        // bound, not the pre-gate class short-circuit).
        var rules = (AcceptanceDefaults.Rules with
        {
            // Story 43-11 AC14: draw from the whole widened dial, not [70,100].
            AutonomyLevel = rng.Next(AutonomyDial.Min, AutonomyDial.Max + 1),
            MaxRevisionRounds = rng.Next(1, 11),
            MaxValidationRepairAttempts = rng.Next(0, 11),
            AmbiguityEscalationThreshold = Math.Round(rng.NextDouble(), 3),
            AlwaysEscalate = Array.Empty<EscalationClass>(),
        }).Validate();

        // Human channel so Reject is a legitimate terminal (not clamped away).
        var channel = ApprovalChannel.User;
        var maxGatePasses = rules.MaxRevisionRounds + 1;

        var rounds = 0;
        var gatePasses = 0;
        var terminated = false;

        while (gatePasses <= maxGatePasses)
        {
            gatePasses++;
            var reviewDecision = (ReviewDecision)rng.Next(0, 3);
            var blocking = rng.Next(0, 2) == 1;
            var ctx = Ctx(rules, reviewDecision, blocking, rounds, channel);

            if (AcceptanceGuardrails.TryPreGate(ctx, out _))
            {
                terminated = true;
                break;
            }

            var proposed = RandomDecision(rng);
            var clamped = AcceptanceGuardrails.Clamp(proposed, ctx);

            if (clamped is AcceptanceDecision.RequestRevision)
            {
                rounds++;
                continue;
            }

            // Accept, Reject (human channel), or Escalate → terminal.
            clamped.Should().Match(d =>
                d is AcceptanceDecision.Accept
                || d is AcceptanceDecision.Reject
                || d is AcceptanceDecision.Escalate);
            terminated = true;
            break;
        }

        terminated.Should().BeTrue(
            $"the gate must terminate within {maxGatePasses} passes (rounds budget {rules.MaxRevisionRounds})");
        gatePasses.Should().BeLessThanOrEqualTo(maxGatePasses);
    }

    private static AcceptanceDecision RandomDecision(Random rng) => rng.Next(0, 4) switch
    {
        0 => new AcceptanceDecision.Accept(),
        1 => new AcceptanceDecision.RequestRevision("revise"),
        2 => new AcceptanceDecision.Reject("no"),
        _ => new AcceptanceDecision.Escalate(AcceptanceEscalationReason.AcceptorJudgment, "unsure"),
    };
}
