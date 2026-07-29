using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b AC2 (SprintPlan) — one rejecting and one accepting fixture per
/// rule, each asserting the named violation code. The story's own named
/// counter-example (committed estimates exceed the stated capacity) is the
/// <c>COMMITMENT_EXCEEDS_CAPACITY</c> case, and its message names both numbers
/// (D7).
/// </summary>
[TestFixture]
public class SprintPlanTypeTests
{
    private static readonly SprintPlanDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    private const string ValidDoc =
        """
        {
          "sprintId": "sprint-2026-08-A",
          "capacity": 10,
          "committed": [
            { "issueId": "issue-7", "ownerRole": "developer", "estimate": 5 },
            { "issueId": "issue-9", "ownerRole": "tester", "estimate": 3 }
          ],
          "carryOver": [
            { "issueId": "issue-3", "reason": "Blocked on key rotation." }
          ]
        }
        """;

    [Test]
    public void Valid_document_passes_every_rule()
    {
        var r = Validate(ValidDoc);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Malformed_payload_is_reported() =>
        Codes(Validate("""{ "committed": "none" }""")).Should().Contain(SprintPlanDocumentType.MalformedPayload);

    // ── SPRINT_ID_MISSING ───────────────────────────────────────────────────

    [Test]
    public void Missing_sprint_id_is_reported()
    {
        var r = Validate(ValidDoc.Replace("sprint-2026-08-A", " "));
        Codes(r).Should().Contain(SprintPlanDocumentType.SprintIdMissing);
    }

    [Test]
    public void Present_sprint_id_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.SprintIdMissing);

    // ── CAPACITY_INVALID ────────────────────────────────────────────────────

    [Test]
    public void Missing_capacity_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"capacity\": 10,", ""));
        Codes(r).Should().Contain(SprintPlanDocumentType.CapacityInvalid);
    }

    [Test]
    public void Non_positive_capacity_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"capacity\": 10", "\"capacity\": 0"));
        Codes(r).Should().Contain(SprintPlanDocumentType.CapacityInvalid);
    }

    [Test]
    public void Positive_capacity_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.CapacityInvalid);

    // ── NO_COMMITTED_ITEMS ──────────────────────────────────────────────────

    [Test]
    public void Empty_committed_set_is_reported()
    {
        var r = Validate("""{ "sprintId": "s", "capacity": 10, "committed": [], "carryOver": [] }""");
        Codes(r).Should().Contain(SprintPlanDocumentType.NoCommittedItems);
    }

    [Test]
    public void Non_empty_committed_set_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.NoCommittedItems);

    // ── COMMITTED_ITEM_MISSING_ISSUE_ID ─────────────────────────────────────

    [Test]
    public void Committed_item_without_issue_id_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"issueId\": \"issue-7\"", "\"issueId\": \"\""));
        Codes(r).Should().Contain(SprintPlanDocumentType.CommittedItemMissingIssueId);
    }

    [Test]
    public void Committed_issue_ids_present_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.CommittedItemMissingIssueId);

    // ── COMMITTED_ITEM_MISSING_OWNER_ROLE / OWNER_ROLE_UNKNOWN ──────────────

    [Test]
    public void Committed_item_without_owner_role_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"ownerRole\": \"developer\"", "\"ownerRole\": \"\""));
        Codes(r).Should().Contain(SprintPlanDocumentType.CommittedItemMissingOwnerRole);
    }

    [Test]
    public void Unknown_owner_role_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"ownerRole\": \"developer\"", "\"ownerRole\": \"wizard\""));
        Codes(r).Should().Contain(SprintPlanDocumentType.OwnerRoleUnknown);
    }

    [Test]
    public void Taxonomy_owner_roles_are_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(SprintPlanDocumentType.CommittedItemMissingOwnerRole);
        codes.Should().NotContain(SprintPlanDocumentType.OwnerRoleUnknown);
    }

    // ── COMMITTED_ITEM_MISSING_ESTIMATE ─────────────────────────────────────

    [Test]
    public void Committed_item_without_estimate_is_reported()
    {
        var r = Validate(ValidDoc.Replace(", \"estimate\": 5", ""));
        Codes(r).Should().Contain(SprintPlanDocumentType.CommittedItemMissingEstimate);
    }

    [Test]
    public void Non_positive_estimate_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"estimate\": 5", "\"estimate\": 0"));
        Codes(r).Should().Contain(SprintPlanDocumentType.CommittedItemMissingEstimate);
    }

    [Test]
    public void Positive_estimates_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.CommittedItemMissingEstimate);

    // ── COMMITMENT_EXCEEDS_CAPACITY (the story's counter-example) ───────────

    [Test]
    public void Committed_estimates_exceeding_capacity_are_rejected()
    {
        var r = Validate(ValidDoc.Replace("\"capacity\": 10", "\"capacity\": 6"));
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(SprintPlanDocumentType.CommitmentExceedsCapacity);
        r.Violations.Single(v => v.Code == SprintPlanDocumentType.CommitmentExceedsCapacity)
            .Message.Should().Contain("8").And.Contain("6",
                "the message names the committed sum and the stated capacity (D7)");
    }

    [Test]
    public void Commitment_within_capacity_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.CommitmentExceedsCapacity);

    [Test]
    public void Capacity_check_does_not_fire_on_an_unsized_committed_set()
    {
        // The arithmetic is undecidable when an estimate is missing — that item
        // already reports COMMITTED_ITEM_MISSING_ESTIMATE; no phantom capacity code.
        var r = Validate(ValidDoc
            .Replace("\"capacity\": 10", "\"capacity\": 1")
            .Replace(", \"estimate\": 5", ""));
        Codes(r).Should().Contain(SprintPlanDocumentType.CommittedItemMissingEstimate);
        Codes(r).Should().NotContain(SprintPlanDocumentType.CommitmentExceedsCapacity);
    }

    // ── CARRYOVER_NOT_FLAGGED ───────────────────────────────────────────────

    [Test]
    public void Carry_over_without_reason_is_reported()
    {
        var r = Validate(ValidDoc.Replace("Blocked on key rotation.", " "));
        Codes(r).Should().Contain(SprintPlanDocumentType.CarryoverNotFlagged);
    }

    [Test]
    public void Flagged_carry_over_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(SprintPlanDocumentType.CarryoverNotFlagged);

    // ── shared contract properties ──────────────────────────────────────────

    [Test]
    public void Contract_is_deterministic()
    {
        var first = Type.RenderContract();
        Type.RenderContract().Should().Be(first);
        first.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Typed_record_round_trips_through_document_json()
    {
        var doc = JsonSerializer.Deserialize<SprintPlan>(ValidDoc, DocumentJson.Options)!;
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        var back = JsonSerializer.Deserialize<SprintPlan>(json, DocumentJson.Options)!;
        back.Should().BeEquivalentTo(doc);
        using var parsed = JsonDocument.Parse(json);
        Type.Validate(parsed.RootElement).IsValid.Should().BeTrue("the re-serialized shape must still validate");
    }
}
