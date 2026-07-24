using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC7 (TestSpec half) — <see cref="TestSpecDocumentType"/> rules:
/// case↔taskId binding, one behavior per case, duplicate collisions flagged. Pure
/// half; the round-trip against <c>TestCaseCreationWorkflow</c>'s accepted
/// <c>testCases</c> token is asserted in Activities.Tests (D8).
/// </summary>
[TestFixture]
public class TestSpecTypeTests
{
    private static readonly TestSpecDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Valid_spec_passes()
    {
        var r = Validate(
            """
            { "testCases": [
              { "id": "TC-1", "taskId": "T-1", "behavior": "returns 429 over the limit" },
              { "id": "TC-2", "taskId": "T-1", "behavior": "allows under the limit" }
            ] }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Empty_spec_is_reported()
    {
        Codes(Validate("""{ "testCases": [] }""")).Should().Contain(TestSpecDocumentType.EmptyTestSpec);
    }

    [Test]
    public void Case_missing_task_id_is_reported()
    {
        var r = Validate("""{ "testCases": [ { "id": "TC-1", "taskId": "", "behavior": "b" } ] }""");
        Codes(r).Should().Contain(TestSpecDocumentType.CaseMissingTaskId);
    }

    [Test]
    public void Case_missing_behavior_is_reported()
    {
        var r = Validate("""{ "testCases": [ { "id": "TC-1", "taskId": "T-1", "behavior": "  " } ] }""");
        Codes(r).Should().Contain(TestSpecDocumentType.CaseMissingBehavior);
    }

    [Test]
    public void Duplicate_task_behavior_pair_is_reported()
    {
        var r = Validate(
            """
            { "testCases": [
              { "id": "TC-1", "taskId": "T-1", "behavior": "returns 429" },
              { "id": "TC-2", "taskId": "T-1", "behavior": "returns 429" }
            ] }
            """);
        Codes(r).Should().Contain(TestSpecDocumentType.DuplicateCaseForBehavior);
    }

    [Test]
    public void Same_behavior_for_different_task_is_not_a_duplicate()
    {
        var r = Validate(
            """
            { "testCases": [
              { "id": "TC-1", "taskId": "T-1", "behavior": "returns 429" },
              { "id": "TC-2", "taskId": "T-2", "behavior": "returns 429" }
            ] }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Serialized_spec_carries_non_empty_testcases_token()
    {
        var spec = new TestSpec { Cases = new[] { new TestCase { TaskId = "T-1", Behavior = "b" } } };
        var json = JsonSerializer.Serialize(spec, DocumentJson.Options);
        json.Should().Contain("\"testCases\"");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("testCases").GetArrayLength().Should().Be(1);
    }

    [Test]
    public void Contract_carries_bound_tokens_and_is_deterministic()
    {
        var contract = Type.RenderContract();
        contract.Should().Contain("\"testCases\"").And.Contain("\"taskId\"").And.Contain("\"behavior\"");
        Type.RenderContract().Should().Be(contract);
    }

    // ── Story 39-15 (D3) — the cross-document task-ID ring via ValidateWithContext ──

    private static DocumentValidationResult ValidateWithContext(string json, string contextJson)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.ValidateWithContext(doc.RootElement, contextJson);
    }

    private const string PlanContext =
        """{ "tasks": [ { "id": "T-1", "files": ["a.cs"], "testing": "unit" } ] }""";

    [Test]
    public void ValidateWithContext_UnknownTaskId_IsRejected()
    {
        var r = ValidateWithContext(
            """{ "testCases": [ { "id": "TC-1", "taskId": "T-9", "behavior": "does a thing" } ] }""",
            PlanContext);
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(TestSpecDocumentType.CaseUnknownTaskId);
        r.Violations.Should().Contain(v => v.Message.Contains("T-9"),
            "the domain-phrased violation names the offending task id for the repair ring");
    }

    [Test]
    public void ValidateWithContext_KnownTaskId_Passes()
    {
        var r = ValidateWithContext(
            """{ "testCases": [ { "id": "TC-1", "taskId": "T-1", "behavior": "does a thing" } ] }""",
            PlanContext);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void ValidateWithContext_EmptyContext_DegradesToPayloadOnly()
    {
        // No plan context — the cross-document rule cannot fire; only payload-only rules apply.
        var r = ValidateWithContext(
            """{ "testCases": [ { "id": "TC-1", "taskId": "T-9", "behavior": "does a thing" } ] }""",
            "");
        Codes(r).Should().NotContain(TestSpecDocumentType.CaseUnknownTaskId);
        r.IsValid.Should().BeTrue();
    }

    [Test]
    public void ValidateWithContext_DefaultDim_FallsBackToValidate()
    {
        // The IDocumentType DIM default returns Validate(payload) — a type without a cross-doc
        // rule is source-compatible (asserted here via a payload-only invalid case).
        IDocumentType type = Type;
        using var doc = JsonDocument.Parse("""{ "testCases": [] }""");
        type.ValidateWithContext(doc.RootElement, "").IsValid.Should().BeFalse();
    }
}
