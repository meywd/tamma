using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC4 — <see cref="PlanDocumentType"/> domain rules (per-task file map,
/// per-task testing, resolvable dependencies via the shared graph check). Pure half;
/// the subsumption/round-trip half lives in Activities.Tests (D8) — its old
/// PlanValidationHelper baseline was retired in Story 39-14.
/// </summary>
[TestFixture]
public class PlanTypeTests
{
    private static readonly PlanDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Valid_two_task_plan_passes()
    {
        var r = Validate(
            """
            {
              "tasks": [
                { "id": "T-1", "description": "a", "files": ["a.cs"], "dependsOn": [], "testing": "unit" },
                { "id": "T-2", "description": "b", "files": ["b.cs"], "dependsOn": ["T-1"], "testing": "integration" }
              ],
              "files": ["a.cs", "b.cs"]
            }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Empty_plan_is_reported()
    {
        Codes(Validate("""{ "tasks": [] }""")).Should().Contain(PlanDocumentType.EmptyPlan);
    }

    [Test]
    public void Task_missing_file_map_is_reported()
    {
        var r = Validate(
            """{ "tasks": [ { "id": "T-1", "description": "a", "files": [], "testing": "unit" } ] }""");
        Codes(r).Should().Contain(PlanDocumentType.TaskMissingFileMap);
    }

    [Test]
    public void Task_missing_testing_is_reported()
    {
        var r = Validate(
            """{ "tasks": [ { "id": "T-1", "description": "a", "files": ["a.cs"], "testing": "" } ] }""");
        Codes(r).Should().Contain(PlanDocumentType.TaskMissingTesting);
    }

    [Test]
    public void Duplicate_task_id_is_reported()
    {
        var r = Validate(
            """
            { "tasks": [
              { "id": "T-1", "description": "a", "files": ["a.cs"], "testing": "unit" },
              { "id": "T-1", "description": "b", "files": ["b.cs"], "testing": "unit" }
            ] }
            """);
        Codes(r).Should().Contain(PlanDocumentType.DuplicateTaskId);
    }

    [Test]
    public void Dangling_dependency_names_the_missing_id()
    {
        var r = Validate(
            """{ "tasks": [ { "id": "T-1", "description": "a", "files": ["a.cs"], "dependsOn": ["T-9"], "testing": "unit" } ] }""");
        r.Violations.Should().Contain(v => v.Code == PlanDocumentType.DanglingDependsOn && v.Message.Contains("T-9"));
    }

    [Test]
    public void Self_dependency_is_reported()
    {
        var r = Validate(
            """{ "tasks": [ { "id": "T-1", "description": "a", "files": ["a.cs"], "dependsOn": ["T-1"], "testing": "unit" } ] }""");
        Codes(r).Should().Contain(PlanDocumentType.SelfDependsOn);
        Codes(r).Should().NotContain(PlanDocumentType.CyclicDependsOn);
    }

    [Test]
    public void Cycle_reports_path_and_no_topological_order()
    {
        var r = Validate(
            """
            { "tasks": [
              { "id": "T-1", "description": "a", "files": ["a.cs"], "dependsOn": ["T-2"], "testing": "unit" },
              { "id": "T-2", "description": "b", "files": ["b.cs"], "dependsOn": ["T-1"], "testing": "unit" }
            ] }
            """);
        Codes(r).Should().Contain(PlanDocumentType.NoTopologicalOrder);
        r.Violations.Should().Contain(v =>
            v.Code == PlanDocumentType.CyclicDependsOn && v.Message.Contains("T-1") && v.Message.Contains("T-2") && v.Message.Contains("->"));
    }

    [Test]
    public void Type_mismatch_is_malformed_payload_not_a_throw()
    {
        var r = Validate("""{ "tasks": "not-an-array" }""");
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Equal(new[] { PlanDocumentType.MalformedPayload });
    }

    [Test]
    public void Contract_carries_bound_tokens_and_is_deterministic()
    {
        var contract = Type.RenderContract();
        contract.Should().Contain("\"tasks\"").And.Contain("\"files\"");
        Type.RenderContract().Should().Be(contract);
    }
}
