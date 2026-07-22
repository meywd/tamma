using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Contract-half tests for the acceptor (Story 39-5 AC2): polymorphic JSON with
/// pinned <c>kind</c> discriminators, the closed derived-type sets (no
/// <c>AutoAccept</c>), the human-only <c>Reject</c> pin, and the D7 no-branch
/// factory pin (every autonomy level yields an orchestrator-bound request of
/// identical shape modulo the rules payload).
/// </summary>
[TestFixture]
public class AcceptanceContractTests
{
    [Test]
    public void AcceptanceDecision_has_exactly_four_derived_types()
    {
        var derived = typeof(AcceptanceDecision).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(AcceptanceDecision)))
            .Select(t => t.Name)
            .ToArray();
        derived.Should().BeEquivalentTo("Accept", "RequestRevision", "Reject", "Escalate");
        derived.Should().NotContain("AutoAccept");
    }

    [Test]
    public void AcceptanceRouting_has_exactly_two_derived_types()
    {
        var derived = typeof(AcceptanceRouting).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(AcceptanceRouting)))
            .Select(t => t.Name)
            .ToArray();
        derived.Should().BeEquivalentTo("DecideSelf", "AssignToRole");
    }

    [TestCase("accept")]
    [TestCase("request-revision")]
    [TestCase("reject")]
    [TestCase("escalate")]
    public void AcceptanceDecision_serializes_with_kind_discriminator(string kind)
    {
        AcceptanceDecision decision = kind switch
        {
            "accept" => new AcceptanceDecision.Accept(),
            "request-revision" => new AcceptanceDecision.RequestRevision("please fix"),
            "reject" => new AcceptanceDecision.Reject("no"),
            _ => new AcceptanceDecision.Escalate(AcceptanceEscalationReason.AcceptorJudgment, "unsure"),
        };

        var json = JsonSerializer.Serialize(decision, AcceptanceRulesJson.Options);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(kind);

        var back = JsonSerializer.Deserialize<AcceptanceDecision>(json, AcceptanceRulesJson.Options);
        back.Should().BeOfType(decision.GetType());
    }

    [TestCase("decide-self")]
    [TestCase("assign-to-role")]
    public void AcceptanceRouting_serializes_with_kind_discriminator(string kind)
    {
        AcceptanceRouting routing = kind == "decide-self"
            ? new AcceptanceRouting.DecideSelf()
            : new AcceptanceRouting.AssignToRole("architect", AssignmentBasis.Initiator);

        var json = JsonSerializer.Serialize(routing, AcceptanceRulesJson.Options);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(kind);

        var back = JsonSerializer.Deserialize<AcceptanceRouting>(json, AcceptanceRulesJson.Options);
        back.Should().BeOfType(routing.GetType());
    }

    [Test]
    public void Escalate_reason_serializes_as_wire_string()
    {
        var json = JsonSerializer.Serialize(
            (AcceptanceDecision)new AcceptanceDecision.Escalate(AcceptanceEscalationReason.RoundsExhausted, "done"),
            AcceptanceRulesJson.Options);
        json.Should().Contain("\"reason\":\"rounds-exhausted\"");
    }

    // ── Factory: D7 no-branch pin ──

    [Test]
    public void Factory_yields_orchestrator_bound_request_for_every_autonomy_level()
    {
        var document = AcceptanceTestData.Envelope(DocumentTypeKey.Plan);
        var review = AcceptanceTestData.Envelope(DocumentTypeKey.Review);
        var lineage = new[] { document };

        AcceptanceRequest? first = null;
        for (var level = 70; level <= 100; level++)
        {
            var rules = new ResolvedAcceptanceRules(
                AcceptanceDefaults.Rules with { AutonomyLevel = level },
                AcceptanceRulesSource.SystemDefault, 1, DocumentTypeKey.Plan.ToWire(), DateTimeOffset.UnixEpoch);

            var req = AcceptanceRequestFactory.Create(document, review, lineage, roundsUsed: 1, rules);

            req.DecisionSessionId.Should().NotBe(Guid.Empty);
            req.Document.Should().Be(document);
            req.Review.Should().Be(review);
            req.RoundsUsed.Should().Be(1);
            req.IssueId.Should().Be(document.IssueId);
            req.Rules.Rules.AutonomyLevel.Should().Be(level);

            // Shape identical modulo the rules payload + the minted session id.
            if (first is null) first = req;
            else
            {
                req.Document.Should().Be(first.Document);
                req.Review.Should().Be(first.Review);
                req.RoundsUsed.Should().Be(first.RoundsUsed);
                req.IssueId.Should().Be(first.IssueId);
            }
        }
    }

    [Test]
    public void Factory_rejects_a_non_review_review_envelope()
    {
        var document = AcceptanceTestData.Envelope(DocumentTypeKey.Plan);
        var notAReview = AcceptanceTestData.Envelope(DocumentTypeKey.Design);
        var rules = AcceptanceTestData.Resolved(DocumentTypeKey.Plan);

        FluentActions.Invoking(() =>
                AcceptanceRequestFactory.Create(document, notAReview, new[] { document }, 0, rules))
            .Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACCEPTANCE_REQUEST.INVALID");
    }
}
