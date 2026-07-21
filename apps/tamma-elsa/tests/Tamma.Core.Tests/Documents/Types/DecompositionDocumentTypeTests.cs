using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-3 AC2 — domain rules for <see cref="DecompositionDocumentType"/>:
/// unique ids, no dangling / self / cyclic dependsOn (cycle path named), 2–8h
/// sizing, and the stable NO_PREREQUISITE_ORDER signal.
/// </summary>
[TestFixture]
public class DecompositionDocumentTypeTests
{
    private static readonly DecompositionDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Valid_graph_passes()
    {
        var r = Validate(
            """
            {
              "summary": "Split into two ordered tasks.",
              "subtasks": [
                { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4, "complexity": "low", "dependsOn": [] },
                { "id": "ST-2", "title": "B", "description": "b", "estimateHours": 8, "complexity": "high", "dependsOn": ["ST-1"] }
              ]
            }
            """);

        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Inclusive_bounds_2h_and_8h_pass()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 2, "complexity": "low" },
              { "id": "ST-2", "title": "B", "description": "b", "estimateHours": 8, "complexity": "low" }
            ] }
            """);

        Codes(r).Should().NotContain(DecompositionDocumentType.SizingOutOfRange);
    }

    [Test]
    public void Duplicate_id_is_reported()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4 },
              { "id": "ST-1", "title": "B", "description": "b", "estimateHours": 4 }
            ] }
            """);

        Codes(r).Should().Contain(DecompositionDocumentType.DuplicateTaskId);
    }

    [Test]
    public void Dangling_dependency_names_the_missing_id()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4, "dependsOn": ["ST-99"] }
            ] }
            """);

        r.Violations.Should().Contain(v =>
            v.Code == DecompositionDocumentType.DanglingDependsOn && v.Message.Contains("ST-99"));
    }

    [Test]
    public void Self_dependency_is_reported()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4, "dependsOn": ["ST-1"] }
            ] }
            """);

        Codes(r).Should().Contain(DecompositionDocumentType.SelfDependsOn);
        // A self-loop is excluded from the cycle graph — it is NOT a CYCLIC violation.
        Codes(r).Should().NotContain(DecompositionDocumentType.CyclicDependsOn);
    }

    [Test]
    public void Two_node_cycle_reports_path_and_no_prerequisite_order()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-2", "title": "A", "description": "a", "estimateHours": 4, "dependsOn": ["ST-4"] },
              { "id": "ST-4", "title": "B", "description": "b", "estimateHours": 4, "dependsOn": ["ST-2"] }
            ] }
            """);

        Codes(r).Should().Contain(DecompositionDocumentType.NoPrerequisiteOrder);
        r.Violations.Should().Contain(v =>
            v.Code == DecompositionDocumentType.CyclicDependsOn &&
            v.Message.Contains("ST-2") && v.Message.Contains("ST-4") && v.Message.Contains("->"));
    }

    [Test]
    public void Three_node_cycle_reports_cyclic_and_no_prerequisite_order()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4, "dependsOn": ["ST-2"] },
              { "id": "ST-2", "title": "B", "description": "b", "estimateHours": 4, "dependsOn": ["ST-3"] },
              { "id": "ST-3", "title": "C", "description": "c", "estimateHours": 4, "dependsOn": ["ST-1"] }
            ] }
            """);

        Codes(r).Should().Contain(DecompositionDocumentType.CyclicDependsOn);
        Codes(r).Should().Contain(DecompositionDocumentType.NoPrerequisiteOrder);
    }

    [TestCase("1.5")]
    [TestCase("9")]
    [TestCase("0")]
    public void Estimate_outside_2_to_8_is_out_of_range(string hours)
    {
        var r = Validate(
            $$"""
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": {{hours}} }
            ] }
            """);

        Codes(r).Should().Contain(DecompositionDocumentType.SizingOutOfRange);
    }

    [Test]
    public void Missing_estimate_defaults_to_zero_and_is_out_of_range()
    {
        var r = Validate(
            """{ "summary": "s", "subtasks": [ { "id": "ST-1", "title": "A", "description": "a" } ] }""");

        Codes(r).Should().Contain(DecompositionDocumentType.SizingOutOfRange);
    }

    [Test]
    public void Unknown_complexity_is_strict()
    {
        var r = Validate(
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4, "complexity": "Trivial." }
            ] }
            """);

        Codes(r).Should().Contain(DecompositionDocumentType.UnknownComplexity);
    }

    [Test]
    public void Missing_summary_is_reported()
    {
        var r = Validate(
            """{ "subtasks": [ { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 4 } ] }""");

        Codes(r).Should().Contain(DecompositionDocumentType.MissingSummary);
    }

    [Test]
    public void Empty_subtasks_is_no_tasks()
    {
        var r = Validate("""{ "summary": "s", "subtasks": [] }""");
        Codes(r).Should().Contain(DecompositionDocumentType.NoTasks);
    }

    [Test]
    public void Empty_id_is_task_missing_id()
    {
        var r = Validate(
            """{ "summary": "s", "subtasks": [ { "id": "", "title": "A", "description": "a", "estimateHours": 4 } ] }""");

        Codes(r).Should().Contain(DecompositionDocumentType.TaskMissingId);
    }

    [Test]
    public void Shell_task_is_reported()
    {
        var r = Validate(
            """{ "summary": "s", "subtasks": [ { "id": "ST-1", "title": "", "description": "", "estimateHours": 4 } ] }""");

        Codes(r).Should().Contain(DecompositionDocumentType.TaskEmptyShell);
    }

    [Test]
    public void Type_mismatch_is_malformed_payload_not_a_throw()
    {
        // "subtasks" as a string is a wire type mismatch → deserialization fails →
        // a single MALFORMED_PAYLOAD violation, never a throw out of Validate.
        var r = Validate("""{ "summary": "s", "subtasks": "not-an-array" }""");

        r.IsValid.Should().BeFalse();
        Codes(r).Should().Equal(new[] { DecompositionDocumentType.MalformedPayload });
    }
}
