using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b AC2 (BacklogOrdering) — one rejecting and one accepting fixture
/// per rule, each asserting the named violation code. The story's own named
/// counter-example (two items at the same rank) is the
/// <c>RANK_DUPLICATED</c> case.
/// </summary>
[TestFixture]
public class BacklogOrderingTypeTests
{
    private static readonly BacklogOrderingDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    private const string ValidDoc =
        """
        {
          "items": [
            { "itemId": "issue-7", "rank": 1, "rationale": "Unblocks two teams.", "value": "high", "effort": "1d" },
            { "itemId": "issue-9", "rank": 2, "rationale": "Customer-visible defect.", "value": "medium", "effort": "2d" }
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
        Codes(Validate("""{ "items": 3 }""")).Should().Contain(BacklogOrderingDocumentType.MalformedPayload);

    // ── NO_ITEMS ────────────────────────────────────────────────────────────

    [Test]
    public void Empty_item_set_is_reported() =>
        Codes(Validate("""{ "items": [] }""")).Should().Contain(BacklogOrderingDocumentType.NoItems);

    [Test]
    public void Non_empty_item_set_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.NoItems);

    // ── ITEM_ID_MISSING ─────────────────────────────────────────────────────

    [Test]
    public void Item_without_id_is_reported()
    {
        var r = Validate(
            """
            { "items": [ { "itemId": "", "rank": 1, "rationale": "r", "value": "v", "effort": "e" } ] }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.ItemIdMissing);
    }

    [Test]
    public void Item_ids_present_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.ItemIdMissing);

    // ── ITEM_ID_DUPLICATED (adversarial review 2026-07-29) ──────────────────

    [Test]
    public void Duplicate_item_ids_are_rejected_with_item_id_duplicated()
    {
        // The reviewer's exact counter-example: the same item at two ranks used to
        // VALIDATE, breaking "total order over the referenced item set".
        var r = Validate(
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 1, "rationale": "a", "value": "v", "effort": "e" },
                { "itemId": "issue-7", "rank": 2, "rationale": "b", "value": "v", "effort": "e" }
              ]
            }
            """);
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(BacklogOrderingDocumentType.ItemIdDuplicated);
        r.Violations.Single(v => v.Code == BacklogOrderingDocumentType.ItemIdDuplicated)
            .Message.Should().Contain("issue-7");
    }

    [Test]
    public void Triplicate_item_id_is_reported_once()
    {
        var r = Validate(
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 1, "rationale": "a", "value": "v", "effort": "e" },
                { "itemId": "issue-7", "rank": 2, "rationale": "b", "value": "v", "effort": "e" },
                { "itemId": "issue-7", "rank": 3, "rationale": "c", "value": "v", "effort": "e" }
              ]
            }
            """);
        r.Violations.Count(v => v.Code == BacklogOrderingDocumentType.ItemIdDuplicated).Should().Be(1,
            "one duplicated id is one defect, not one per extra occurrence (the CRITERION_ID_DUPLICATED pattern)");
    }

    [Test]
    public void Distinct_item_ids_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.ItemIdDuplicated);

    // ── RANK_DUPLICATED (the story's named counter-example) ─────────────────

    [Test]
    public void Two_items_at_the_same_rank_are_rejected_with_rank_duplicated()
    {
        var r = Validate(
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 1, "rationale": "a", "value": "v", "effort": "e" },
                { "itemId": "issue-9", "rank": 1, "rationale": "b", "value": "v", "effort": "e" }
              ]
            }
            """);
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(BacklogOrderingDocumentType.RankDuplicated);
        r.Violations.Single(v => v.Code == BacklogOrderingDocumentType.RankDuplicated)
            .Message.Should().Contain("issue-7").And.Contain("issue-9").And.Contain("1",
                "the message names both item ids and the rank (D7)");
    }

    [Test]
    public void Distinct_ranks_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.RankDuplicated);

    // ── RANK_NOT_TOTAL_ORDER ────────────────────────────────────────────────

    [Test]
    public void Gapped_ranks_are_reported()
    {
        var r = Validate(
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 1, "rationale": "a", "value": "v", "effort": "e" },
                { "itemId": "issue-9", "rank": 3, "rationale": "b", "value": "v", "effort": "e" }
              ]
            }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.RankNotTotalOrder);
    }

    [Test]
    public void Non_one_based_ranks_are_reported()
    {
        var r = Validate(
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 0, "rationale": "a", "value": "v", "effort": "e" },
                { "itemId": "issue-9", "rank": 1, "rationale": "b", "value": "v", "effort": "e" }
              ]
            }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.RankNotTotalOrder);
    }

    [Test]
    public void Missing_rank_is_reported()
    {
        var r = Validate(
            """
            { "items": [ { "itemId": "issue-7", "rationale": "a", "value": "v", "effort": "e" } ] }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.RankNotTotalOrder);
    }

    [Test]
    public void Gap_free_one_based_ranks_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.RankNotTotalOrder);

    // ── ITEM_MISSING_RATIONALE ──────────────────────────────────────────────

    [Test]
    public void Item_without_rationale_is_reported()
    {
        var r = Validate(
            """
            { "items": [ { "itemId": "issue-7", "rank": 1, "rationale": " ", "value": "v", "effort": "e" } ] }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.ItemMissingRationale);
    }

    [Test]
    public void Rationale_present_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.ItemMissingRationale);

    // ── ITEM_MISSING_ESTIMATE ───────────────────────────────────────────────

    [Test]
    public void Item_without_value_estimate_is_reported()
    {
        var r = Validate(
            """
            { "items": [ { "itemId": "issue-7", "rank": 1, "rationale": "r", "value": "", "effort": "e" } ] }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.ItemMissingEstimate);
    }

    [Test]
    public void Item_without_effort_estimate_is_reported()
    {
        var r = Validate(
            """
            { "items": [ { "itemId": "issue-7", "rank": 1, "rationale": "r", "value": "v", "effort": "" } ] }
            """);
        Codes(r).Should().Contain(BacklogOrderingDocumentType.ItemMissingEstimate);
    }

    [Test]
    public void Both_estimates_present_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(BacklogOrderingDocumentType.ItemMissingEstimate);

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
        var doc = JsonSerializer.Deserialize<BacklogOrdering>(ValidDoc, DocumentJson.Options)!;
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        var back = JsonSerializer.Deserialize<BacklogOrdering>(json, DocumentJson.Options)!;
        back.Should().BeEquivalentTo(doc);
        using var parsed = JsonDocument.Parse(json);
        Type.Validate(parsed.RootElement).IsValid.Should().BeTrue("the re-serialized shape must still validate");
    }
}
