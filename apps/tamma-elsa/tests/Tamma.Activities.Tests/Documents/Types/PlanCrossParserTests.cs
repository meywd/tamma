using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using TypedPlan = Tamma.Core.Documents.Types.Plan;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC4/AC8 — the subsumption + round-trip half for <see cref="PlanDocumentType"/>
/// (Design Decision D8). The OLD <c>PlanValidationHelper.ValidatePlan</c> baseline this suite
/// once cross-checked against was DELETED in Story 39-14 (its bespoke retry loop was subsumed
/// by the document lifecycle); the recorded baseline verdicts are pinned inline as comments,
/// and the assertions now stand on the typed validator alone: every input the old checker
/// rejected the typed validator also rejects, and a valid fixture round-trips through the
/// typed payload back to JSON the typed validator still accepts (root <c>files</c> preserved
/// per D5).
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

    // JSON-shaped negatives the retired PlanValidationHelper.ValidatePlan rejected.
    [TestCase("{}")]                                            // old: "Empty plan"
    [TestCase("""{"fileMap": {"src/foo.ts": "create"}}""")]      // old: MissingTasksField
    [TestCase("""{"tasks": [{"id": "T1"}]}""")]                  // old: MissingFileMapField (no per-task files/testing)
    public void Every_plan_the_baseline_rejected_the_typed_validator_also_rejects(string json)
    {
        Validate(json).IsValid.Should().BeFalse("the typed validator must reject what the old ValidatePlan rejected");
    }

    [Test]
    public void Text_level_negative_throws_at_the_json_boundary()
    {
        // "{not valid json}" → the old ValidatePlan reported "Invalid JSON"; in the typed
        // pipeline it never reaches Validate — it fails loud at the JSON parse boundary (D8).
        const string malformed = "{not valid json}";

        var act = () => JsonDocument.Parse(malformed);
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void Valid_plan_round_trips_through_the_typed_payload()
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

        // The old ValidatePlan accepted this fixture; the typed validator accepts it too.
        Validate(fixture).IsValid.Should().BeTrue("typed validator accepts the fixture");

        // Deserialize → re-serialize → the typed validator still passes (root files preserved, D5).
        var typed = JsonSerializer.Deserialize<TypedPlan>(fixture, DocumentJson.Options)!;
        var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);

        Validate(reserialized).IsValid.Should().BeTrue("the re-serialized typed payload must still validate");
    }
}
