using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using TypedPlan = Tamma.Core.Documents.Types.Plan;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC4/AC8 — the subsumption + round-trip half for <see cref="PlanDocumentType"/>
/// (Design Decision D8). Invokes the OLD <c>PlanValidationHelper.ValidatePlan</c>
/// baseline: every JSON-shaped input it rejects, the typed validator also rejects; and
/// a fixture BOTH pass round-trips through the typed payload back to JSON the old
/// checker still passes (root <c>files</c> preserved per D5).
/// </summary>
[TestFixture]
public class PlanCrossParserTests
{
    private static readonly PlanDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    // JSON-shaped negatives from PlanValidationTests.cs.
    [TestCase("{}")]                                            // EmptyJsonObject → "Empty plan"
    [TestCase("""{"fileMap": {"src/foo.ts": "create"}}""")]      // MissingTasksField
    [TestCase("""{"tasks": [{"id": "T1"}]}""")]                  // MissingFileMapField (no per-task files/testing)
    public void Every_plan_the_baseline_rejects_the_typed_validator_also_rejects(string json)
    {
        var (_, isValid, _) = PlanValidationHelper.ValidatePlan(json);
        isValid.Should().BeFalse("baseline floor");

        Validate(json).IsValid.Should().BeFalse("the typed validator must also reject what ValidatePlan rejects");
    }

    [Test]
    public void Text_level_negative_throws_at_the_json_boundary()
    {
        // "{not valid json}" → ValidatePlan reports "Invalid JSON"; in the typed pipeline
        // it never reaches Validate — it fails loud at the JSON parse boundary (D8).
        const string malformed = "{not valid json}";
        PlanValidationHelper.ValidatePlan(malformed).isValid.Should().BeFalse("baseline floor");

        var act = () => JsonDocument.Parse(malformed);
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void Valid_plan_round_trips_back_through_the_old_checker()
    {
        const string fixture =
            """
            {
              "tasks": [
                { "id": "T-1", "description": "Add users table", "files": ["db/001.sql"], "dependsOn": [], "testing": "migration applies" },
                { "id": "T-2", "description": "Login endpoint", "files": ["src/Login.cs"], "dependsOn": ["T-1"], "testing": "integration 200" }
              ],
              "files": ["db/001.sql", "src/Login.cs"]
            }
            """;

        // Both parsers accept it.
        PlanValidationHelper.ValidatePlan(fixture).isValid.Should().BeTrue("baseline accepts the fixture");
        Validate(fixture).IsValid.Should().BeTrue("typed validator accepts the fixture");

        // Deserialize → re-serialize → the OLD checker still passes (root files preserved).
        var typed = JsonSerializer.Deserialize<TypedPlan>(fixture, DocumentJson.Options)!;
        var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);

        var (_, stillValid, errors) = PlanValidationHelper.ValidatePlan(reserialized);
        stillValid.Should().BeTrue($"the old checker must still pass the re-serialized typed payload (errors: {errors})");
    }
}
