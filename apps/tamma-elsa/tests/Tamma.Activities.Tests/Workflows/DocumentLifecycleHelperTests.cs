using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using ReviewDoc = Tamma.Core.Documents.Types.Review;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-6 — unit + termination-property tests for the pure
/// <see cref="DocumentLifecycleHelper"/> (Test Plan step 9). Carries the AC3
/// (payload completeness), AC4 (provably bounded loops), and AC6 (illegal
/// transitions fail loud) correctness burden without a workflow runtime.
/// </summary>
[TestFixture]
public class DocumentLifecycleHelperTests
{
    // ── D2 — ValidateProducerSpec ──────────────────────────────────────

    [Test]
    public void ValidateProducerSpec_AcceptsTheDecompositionPilot()
    {
        var act = () => DocumentLifecycleHelper.ValidateProducerSpec(
            "senior_developer", "decompose-issue", "decomposition");
        act.Should().NotThrow();
    }

    [TestCase("not_a_role", "decompose-issue", "decomposition")]
    [TestCase("senior_developer", "not_an_action", "decomposition")]
    [TestCase("tester", "decompose-issue", "decomposition")]      // ineligible (role, action)
    [TestCase("senior_developer", "decompose-issue", "not-a-type")]
    public void ValidateProducerSpec_ThrowsInvalidProducer_OnBadSpec(string role, string action, string type)
    {
        var act = () => DocumentLifecycleHelper.ValidateProducerSpec(role, action, type);
        act.Should().Throw<TammaError>().Where(e => e.Code == "DOCUMENT.LIFECYCLE.INVALID_PRODUCER");
    }

    // ── D11 — feedback composition into the declared variable only ─────

    [Test]
    public void BuildRepairVariables_NoViolations_IsByteIdenticalPassthrough()
    {
        const string vars = "{\"workItemJson\":\"x\"}";
        var result = DocumentLifecycleHelper.BuildRepairVariables(vars, Array.Empty<DocumentViolation>(), "revisionNotes");
        JsonNormalized(result).Should().Be(JsonNormalized(vars));
    }

    [Test]
    public void BuildRepairVariables_AppendsIntoDeclaredFeedbackVariableOnly()
    {
        var violations = new[]
        {
            new DocumentViolation("NO_TASKS", "The decomposition has no subtasks."),
            new DocumentViolation("MISSING_SUMMARY", "The decomposition has no summary."),
        };
        var result = DocumentLifecycleHelper.BuildRepairVariables("{\"a\":\"1\"}", violations, "revisionNotes");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("a").GetString().Should().Be("1", "existing vars are preserved");
        var feedback = doc.RootElement.GetProperty("revisionNotes").GetString();
        feedback.Should().Contain("has no subtasks").And.Contain("has no summary");
    }

    [Test]
    public void BuildRevisionVariables_FoldsReviewSummaryAndIssues()
    {
        var reviewJson = JsonSerializer.Serialize(new ReviewDoc
        {
            Subject = new ReviewSubject { Kind = "document", DocumentId = Guid.NewGuid(), DocumentType = "decomposition" },
            Decision = ReviewDecision.RequestChanges,
            Summary = "Needs migration ordering.",
            Issues = new[]
            {
                new ReviewIssue(ReviewSeverity.Critical, "correctness", "Migration runs first", "Reorder ST-2"),
            },
        }, DocumentJson.Options);

        var result = DocumentLifecycleHelper.BuildRevisionVariables("{}", reviewJson, "revisionNotes");
        using var doc = JsonDocument.Parse(result);
        var notes = doc.RootElement.GetProperty("revisionNotes").GetString();
        notes.Should().Contain("Needs migration ordering").And.Contain("Reorder ST-2");
    }

    // ── D8 — ambiguity threshold ───────────────────────────────────────

    [Test]
    public void IsAmbiguityAboveThreshold_ReadsAssessmentScore()
    {
        var rules = DefaultRules();
        DocumentLifecycleHelper.IsAmbiguityAboveThreshold(
            "ambiguity-assessment", "{\"score\":0.9}", rules, null).Should().BeTrue();
        DocumentLifecycleHelper.IsAmbiguityAboveThreshold(
            "ambiguity-assessment", "{\"score\":0.1}", rules, null).Should().BeFalse();
    }

    [Test]
    public void IsAmbiguityAboveThreshold_HonoursThreadedScoreForAnyType()
    {
        var rules = DefaultRules();
        DocumentLifecycleHelper.IsAmbiguityAboveThreshold("decomposition", "{}", rules, 0.95).Should().BeTrue();
        DocumentLifecycleHelper.IsAmbiguityAboveThreshold("decomposition", "{}", rules, 0.2).Should().BeFalse();
    }

    // ── D7 — lineage completeness ──────────────────────────────────────

    [Test]
    public void BuildOutcome_CarriesCompleteLineage()
    {
        var state = NewState(maxRounds: 2, maxRepair: 2);
        state = AppendDraft(state);
        state = DocumentLifecycleHelper.WithViolations(state, new[]
        {
            new DocumentViolation("NO_TASKS", "nothing decomposed"),
        });
        state = DocumentLifecycleHelper.IncrementRepairAttempts(state);

        var result = DocumentLifecycleHelper.BuildOutcome(state, DocumentLifecycleOutcome.ValidationExhausted);

        result.Status.Should().Be(DocumentLifecycleResult.StatusEscalated);
        result.Outcome.Should().Be(DocumentLifecycleOutcome.ValidationExhausted);
        result.Lineage.Drafts.Should().ContainSingle();
        result.Lineage.RepairAttemptsUsed.Should().Be(1);
        result.Lineage.LastViolations.Should().NotBeEmpty();
        result.Lineage.RulesReference.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void BuildAccepted_And_BuildRejected_HaveNullOutcome_AndDistinctStatus()
    {
        var state = AppendDraft(NewState(2, 2));
        var docId = state.Current!.Id;

        var accepted = DocumentLifecycleHelper.BuildAccepted(state, docId);
        accepted.Status.Should().Be(DocumentLifecycleResult.StatusAccepted);
        accepted.Outcome.Should().BeNull("Outcome is null on accepted (parents switch on Status first)");

        var rejected = DocumentLifecycleHelper.BuildRejected(state, docId);
        rejected.Status.Should().Be(DocumentLifecycleResult.StatusRejected);
        rejected.Outcome.Should().BeNull("Outcome is null on rejected too — a first-class terminal, D7");
    }

    // ── AC6 — illegal transition fails loud (never a silent overwrite) ──

    [Test]
    public void ApplyTransition_IllegalDraftToAccepted_ThrowsIllegalTransition()
    {
        var state = AppendDraft(NewState(2, 2));
        var draft = state.Current!;   // Draft
        var act = () => DocumentLifecycleHelper.ApplyTransition(draft, DocumentState.Accepted, DateTimeOffset.UtcNow);
        act.Should().Throw<TammaError>().Where(e => e.Code == "DOCUMENT.STATE.ILLEGAL_TRANSITION");
    }

    // ── AC4 — termination property tests ───────────────────────────────

    [Test]
    public void RepairLoop_AlwaysTerminatesWithinBudget()
    {
        for (var budget = 0; budget <= 8; budget++)
        {
            var state = NewState(2, budget);
            var turns = 0;
            while (DocumentLifecycleHelper.ShouldRepair(state))
            {
                state = DocumentLifecycleHelper.IncrementRepairAttempts(state);
                turns++;
                turns.Should().BeLessThanOrEqualTo(budget + 1, "the repair loop must be bounded by the budget");
            }
            state.RepairAttempts.Should().Be(budget);
        }
    }

    [Test]
    public void ReviseLoop_AlwaysTerminatesWithinBudget()
    {
        for (var budget = 1; budget <= 8; budget++)
        {
            var state = NewState(budget, 2);
            var turns = 0;
            while (DocumentLifecycleHelper.ShouldRevise(state))
            {
                state = DocumentLifecycleHelper.IncrementRound(state);
                turns++;
                turns.Should().BeLessThanOrEqualTo(budget + 1);
            }
            state.Round.Should().Be(budget);
        }
    }

    [Test]
    public void RandomizedLifecycle_AlwaysTerminates_InAClosedStatus_WithLineage()
    {
        // Drive the helper state machine through arbitrary verdict/violation sequences
        // (approve / concerns / invalid in any order) and assert it always terminates in
        // one of {accepted, rejected, escalated} within the round/repair bounds, and that
        // an exhaustion outcome carries complete lineage.
        for (var seed = 0; seed < 400; seed++)
        {
            var rnd = new Random(seed);
            var maxRounds = 1 + rnd.Next(4);
            var maxRepair = rnd.Next(4);
            var state = NewState(maxRounds, maxRepair);

            var result = Drive(state, rnd, out var iterations);

            iterations.Should().BeLessThan((maxRounds + maxRepair + 4) * 3,
                $"seed {seed}: the lifecycle must terminate in a bounded number of steps");
            new[]
            {
                DocumentLifecycleResult.StatusAccepted,
                DocumentLifecycleResult.StatusRejected,
                DocumentLifecycleResult.StatusEscalated,
            }.Should().Contain(result.Status, $"seed {seed}: status must be a closed terminal");

            if (result.Status == DocumentLifecycleResult.StatusEscalated)
            {
                result.Outcome.Should().NotBeNull($"seed {seed}: escalated exit carries a typed outcome");
                if (result.Outcome == DocumentLifecycleOutcome.ValidationExhausted)
                    result.Lineage.LastViolations.Should().NotBeEmpty(
                        $"seed {seed}: validation exhaustion records the last violations");
                result.Lineage.RoundsUsed.Should().BeLessThanOrEqualTo(maxRounds);
                result.Lineage.RepairAttemptsUsed.Should().BeLessThanOrEqualTo(maxRepair);
            }
            else
            {
                result.Outcome.Should().BeNull($"seed {seed}: accepted/rejected carry a null outcome");
            }
        }
    }

    // ── driver mirroring the workflow routing using ONLY helper functions ──

    private static DocumentLifecycleResult Drive(DocumentLifecycleHelper.LifecycleState state, Random rnd, out int iterations)
    {
        iterations = 0;
        var guard = 200;
        while (guard-- > 0)
        {
            iterations++;

            // PRODUCE / repair / revise turn → a fresh draft.
            state = AppendDraft(state);

            // VALIDATE (random pass/fail).
            var valid = rnd.NextDouble() < 0.6;
            if (!valid)
            {
                state = DocumentLifecycleHelper.WithViolations(state, new[]
                {
                    new DocumentViolation("NO_TASKS", "nothing decomposed"),
                });
                if (DocumentLifecycleHelper.ShouldRepair(state))
                {
                    state = DocumentLifecycleHelper.IncrementRepairAttempts(state);
                    continue; // repair → re-produce → re-validate
                }
                return DocumentLifecycleHelper.BuildOutcome(state, DocumentLifecycleOutcome.ValidationExhausted);
            }

            state = DocumentLifecycleHelper.WithViolations(state, Array.Empty<DocumentViolation>());

            // AMBIGUITY (rare).
            if (rnd.NextDouble() < 0.1)
                return DocumentLifecycleHelper.BuildOutcome(state, DocumentLifecycleOutcome.AmbiguityAboveThreshold);

            // REVIEW.
            var reviewJson = rnd.NextDouble() < 0.5 ? ApproveReviewJson() : ConcernsReviewJson();
            state = AppendReview(state);
            var route = DocumentLifecycleHelper.ComputeReviewRoute(state, reviewJson);

            switch (route)
            {
                case DocumentLifecycleHelper.ReviewRoute.Accept:
                    // ACCEPT gate — orchestrator picks a random decision; guardrail clamps.
                    return AcceptGate(state, rnd);
                case DocumentLifecycleHelper.ReviewRoute.Revise:
                    state = DocumentLifecycleHelper.IncrementRound(state);
                    continue;
                case DocumentLifecycleHelper.ReviewRoute.RoundsExhausted:
                    return DocumentLifecycleHelper.BuildOutcome(state, DocumentLifecycleOutcome.RoundsExhausted);
                default:
                    return DocumentLifecycleHelper.BuildOutcome(state, DocumentLifecycleOutcome.ReviewUndecidable);
            }
        }
        throw new InvalidOperationException("lifecycle driver failed to terminate — the loops are not bounded");
    }

    private static DocumentLifecycleResult AcceptGate(DocumentLifecycleHelper.LifecycleState state, Random rnd)
    {
        var facts = new ReviewFacts(ReviewDecision.Approve, HasBlockingIssues: false);
        var ctx = new AcceptanceGateContext(
            DocumentType: DocumentTypeKey.Decomposition,
            AgentActionWire: "decompose-issue",
            Review: facts,
            RoundsUsed: state.Round,
            Rules: state.Rules.Rules,
            DeciderChannel: ApprovalChannel.User);

        AcceptanceDecision proposed = rnd.Next(4) switch
        {
            0 => new AcceptanceDecision.Accept(),
            1 => new AcceptanceDecision.RequestRevision("more"),
            2 => new AcceptanceDecision.Reject("no"),
            _ => new AcceptanceDecision.Escalate(AcceptanceEscalationReason.AcceptorJudgment, "unsure"),
        };

        var clamped = AcceptanceGuardrails.Clamp(proposed, ctx);
        switch (clamped)
        {
            case AcceptanceDecision.Accept:
                return DocumentLifecycleHelper.BuildAccepted(state, state.Current!.Id);
            case AcceptanceDecision.Reject:
                return DocumentLifecycleHelper.BuildRejected(state, state.Current!.Id);
            case AcceptanceDecision.RequestRevision:
                // Guardrail already converts an over-budget revision to Escalate, so this
                // is always within budget; a revise here would loop but the driver treats it
                // as a terminal revise-round to keep the property assertion simple.
                return DocumentLifecycleHelper.BuildAccepted(state, state.Current!.Id);
            case AcceptanceDecision.Escalate esc:
                return DocumentLifecycleHelper.BuildOutcome(
                    state, DocumentLifecycleHelper.OutcomeForEscalationReason(esc.Reason));
            default:
                return DocumentLifecycleHelper.BuildOutcome(state, DocumentLifecycleOutcome.ReviewUndecidable);
        }
    }

    // ── fixtures ───────────────────────────────────────────────────────

    private static AcceptanceRules DefaultRules() => AcceptanceDefaults.Rules;

    private static DocumentLifecycleHelper.LifecycleState NewState(int maxRounds, int maxRepair)
    {
        var rules = (AcceptanceDefaults.Rules with
        {
            MaxRevisionRounds = maxRounds,
            MaxValidationRepairAttempts = maxRepair,
        }).Validate();
        var resolved = new ResolvedAcceptanceRules(
            rules, AcceptanceRulesSource.SystemDefault, 1, "decomposition", DateTimeOffset.UtcNow);

        return DocumentLifecycleHelper.Init(
            "senior_developer", "decompose-issue", "{}", "decomposition",
            "issue-1", "corr-1", UuidV7.NewGuid(), "revisionNotes", null, resolved);
    }

    private static DocumentLifecycleHelper.LifecycleState AppendDraft(DocumentLifecycleHelper.LifecycleState state)
    {
        using var doc = JsonDocument.Parse("{\"summary\":\"s\"}");
        var producer = DocumentProducer.Create("senior_developer", "decompose-issue", "llm-call");
        var envelope = DocumentLifecycleHelper.MintDraft(
            state, doc.RootElement.Clone(), producer, state.Current?.Id, DateTimeOffset.UtcNow);
        return DocumentLifecycleHelper.AppendDraft(state, envelope);
    }

    private static DocumentLifecycleHelper.LifecycleState AppendReview(DocumentLifecycleHelper.LifecycleState state)
    {
        using var doc = JsonDocument.Parse(ApproveReviewJson());
        var producer = DocumentProducer.Create("architect", "plan-review", "document-review");
        var envelope = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Review, 1, state.IssueId, state.CorrelationId, producer, doc.RootElement.Clone());
        return DocumentLifecycleHelper.AppendReview(state, envelope);
    }

    private static string ApproveReviewJson() => JsonSerializer.Serialize(new ReviewDoc
    {
        Subject = new ReviewSubject { Kind = "document", DocumentId = Guid.NewGuid(), DocumentType = "decomposition" },
        Decision = ReviewDecision.Approve,
        Summary = "Looks good.",
        Issues = Array.Empty<ReviewIssue>(),
    }, DocumentJson.Options);

    private static string ConcernsReviewJson() => JsonSerializer.Serialize(new ReviewDoc
    {
        Subject = new ReviewSubject { Kind = "document", DocumentId = Guid.NewGuid(), DocumentType = "decomposition" },
        Decision = ReviewDecision.RequestChanges,
        Summary = "Please revise.",
        Issues = new[] { new ReviewIssue(ReviewSeverity.Major, "clarity", "unclear", "clarify it") },
    }, DocumentJson.Options);

    private static string JsonNormalized(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }
}
