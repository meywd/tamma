using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b D5 — the cross-document rules ride
/// <see cref="IDocumentType.ValidateWithContext"/> and ship INERT: with an empty
/// context every 41-1b type's ValidateWithContext is byte-identical to its
/// context-free Validate (the DIM default holds); with a populated context,
/// AcceptanceCriteria rejects a criterion naming scope absent from the supplied
/// decomposition (<c>CRITERION_REFERENCES_UNPLANNED_SCOPE</c>) and UxSpec rejects
/// a flow with no matching acceptance criterion
/// (<c>FLOW_UNMAPPED_TO_ACCEPTANCE_CRITERION</c>) — the TestSpec precedent
/// (39-15 D3).
/// </summary>
[TestFixture]
public class DocumentTypesCrossDocumentValidationTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    // ── the DIM default: empty context ≡ context-free Validate, for all six ──

    [Test]
    public void Empty_context_is_a_no_op_for_every_41_1b_type()
    {
        var samples = new (IDocumentType Type, string Payload)[]
        {
            (new AcceptanceCriteriaDocumentType(), """{ "issueId": "", "criteria": [] }"""),
            (new BacklogOrderingDocumentType(), """{ "items": [] }"""),
            (new SprintPlanDocumentType(), """{ "sprintId": "", "committed": [] }"""),
            (new TestPlanDocumentType(), """{ "scope": "", "riskAreas": [] }"""),
            (new ThreatModelDocumentType(), """{ "assets": [], "threats": [] }"""),
            (new UxSpecDocumentType(), """{ "flows": [] }"""),
        };

        foreach (var (type, payload) in samples)
        {
            var element = Parse(payload);
            var direct = type.Validate(element);
            foreach (var context in new[] { "", "   ", null as string })
            {
                var withContext = type.ValidateWithContext(element, context!);
                withContext.Should().BeEquivalentTo(direct,
                    $"{type.Key}.ValidateWithContext with an empty context must equal Validate");
            }
        }
    }

    // ── AcceptanceCriteria × Decomposition ──────────────────────────────────

    private const string ValidCriteria =
        """
        {
          "issueId": "issue-42",
          "criteria": [
            { "id": "AC-1", "form": "checklist", "statement": "s", "verifiable": true, "scopeRef": "ST-1" }
          ]
        }
        """;

    private const string DecompositionContext =
        """
        {
          "summary": "split",
          "subtasks": [
            { "id": "ST-1", "title": "t", "description": "d", "acceptanceCriteria": "a", "estimateHours": 4, "complexity": "medium", "dependsOn": [] }
          ]
        }
        """;

    [Test]
    public void Criterion_mapped_to_planned_scope_is_accepted()
    {
        var r = new AcceptanceCriteriaDocumentType().ValidateWithContext(Parse(ValidCriteria), DecompositionContext);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Criterion_referencing_unplanned_scope_is_rejected()
    {
        var payload = Parse(ValidCriteria.Replace("\"scopeRef\": \"ST-1\"", "\"scopeRef\": \"ST-99\""));
        var r = new AcceptanceCriteriaDocumentType().ValidateWithContext(payload, DecompositionContext);
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.CriterionReferencesUnplannedScope);
    }

    [Test]
    public void Unreadable_decomposition_context_degrades_to_payload_only()
    {
        var payload = Parse(ValidCriteria.Replace("\"scopeRef\": \"ST-1\"", "\"scopeRef\": \"ST-99\""));
        var r = new AcceptanceCriteriaDocumentType().ValidateWithContext(payload, "not json at all");
        r.IsValid.Should().BeTrue("an unreadable context must degrade, never throw or phantom-fail");
    }

    // ── UxSpec × AcceptanceCriteria ─────────────────────────────────────────

    private const string ValidUxSpec =
        """
        {
          "flows": [
            {
              "id": "F1",
              "name": "sign in",
              "entryState": "landing",
              "successState": "dashboard",
              "errorStates": ["error banner"],
              "acceptanceCriteriaRefs": ["AC-1"]
            }
          ],
          "screens": []
        }
        """;

    private const string CriteriaContext =
        """
        {
          "issueId": "issue-42",
          "criteria": [
            { "id": "AC-1", "form": "checklist", "statement": "s", "verifiable": true }
          ]
        }
        """;

    [Test]
    public void Flow_mapped_to_an_existing_criterion_is_accepted()
    {
        var r = new UxSpecDocumentType().ValidateWithContext(Parse(ValidUxSpec), CriteriaContext);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Flow_with_no_matching_criterion_is_rejected()
    {
        var payload = Parse(ValidUxSpec.Replace("\"acceptanceCriteriaRefs\": [\"AC-1\"]", "\"acceptanceCriteriaRefs\": [\"AC-9\"]"));
        var r = new UxSpecDocumentType().ValidateWithContext(payload, CriteriaContext);
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(UxSpecDocumentType.FlowUnmappedToAcceptanceCriterion);
    }

    [Test]
    public void Flow_with_no_refs_at_all_is_rejected_when_criteria_are_supplied()
    {
        var payload = Parse(ValidUxSpec.Replace("\"acceptanceCriteriaRefs\": [\"AC-1\"]", "\"acceptanceCriteriaRefs\": []"));
        var r = new UxSpecDocumentType().ValidateWithContext(payload, CriteriaContext);
        Codes(r).Should().Contain(UxSpecDocumentType.FlowUnmappedToAcceptanceCriterion);
    }

    [Test]
    public void Unreadable_criteria_context_degrades_to_payload_only()
    {
        var payload = Parse(ValidUxSpec.Replace("\"acceptanceCriteriaRefs\": [\"AC-1\"]", "\"acceptanceCriteriaRefs\": []"));
        var r = new UxSpecDocumentType().ValidateWithContext(payload, "{ broken");
        r.IsValid.Should().BeTrue("an unreadable context must degrade, never throw or phantom-fail");
    }
}
