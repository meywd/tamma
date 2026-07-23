using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using TypedDecomposition = Tamma.Core.Documents.Types.Decomposition;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-12 — property-level coverage for <see cref="DecompositionBindingHelper"/>, the
/// pure fail-closed core of the issue-decomposition lifecycle binding. Covers AC2 (exit
/// mapping), AC4 (accepted-payload half), AC5 (consumer-shape half).
/// </summary>
[TestFixture]
public class DecompositionBindingHelperTests
{
    private static readonly string ValidPayload =
        "{\"summary\":\"Split rate limiting into middleware then config.\",\"subtasks\":[" +
        "{\"id\":\"ST-1\",\"title\":\"Middleware\",\"description\":\"limiter\",\"estimateHours\":6,\"complexity\":\"medium\",\"dependsOn\":[]}," +
        "{\"id\":\"ST-2\",\"title\":\"Config\",\"description\":\"per-tenant\",\"estimateHours\":4,\"complexity\":\"low\",\"dependsOn\":[\"ST-1\"]}]}";

    // ── ReadLifecycleResult — boxed / string / JsonElement matrix ────────

    [Test]
    public void ReadLifecycleResult_BoxedStrings_ReadsAcceptedExit()
    {
        var exit = DecompositionBindingHelper.ReadLifecycleResult(new Dictionary<string, object>
        {
            ["status"] = "accepted",
            ["outcome"] = "",
            ["documentId"] = "0192a8b0-1111-7abc-8def-000000000001",
            ["documentJson"] = ValidPayload,
        });

        exit.Status.Should().Be("accepted");
        exit.Outcome.Should().BeNull("outcome is null on acceptance");
        exit.DocumentId.Should().Be("0192a8b0-1111-7abc-8def-000000000001");
        exit.DocumentJson.Should().Be(ValidPayload);
        DecompositionBindingHelper.IsAccepted(exit).Should().BeTrue();
    }

    [Test]
    public void ReadLifecycleResult_JsonElementValues_ReadsEscalatedExit()
    {
        using var status = JsonDocument.Parse("\"escalated\"");
        using var outcome = JsonDocument.Parse("\"validation-exhausted\"");
        using var body = JsonDocument.Parse("\"{}\"");

        var exit = DecompositionBindingHelper.ReadLifecycleResult(new Dictionary<string, object>
        {
            ["status"] = status.RootElement.Clone(),
            ["outcome"] = outcome.RootElement.Clone(),
            ["documentJson"] = body.RootElement.Clone(),
        });

        exit.Status.Should().Be("escalated");
        exit.Outcome.Should().Be("validation-exhausted");
        DecompositionBindingHelper.IsAccepted(exit).Should().BeFalse();
    }

    [Test]
    public void ReadLifecycleResult_NullDictionary_FailsClosedToEscalated()
    {
        var exit = DecompositionBindingHelper.ReadLifecycleResult(null);
        exit.Status.Should().Be(DocumentLifecycleResult.StatusEscalated,
            "a missing result must fail closed to an escalation — never a silent accepted");
        exit.Outcome.Should().Be(DocumentLifecycleOutcome.ValidationExhausted.ToWire());
        exit.DocumentJson.Should().Be("{}");
        DecompositionBindingHelper.IsAccepted(exit).Should().BeFalse();
    }

    [Test]
    public void ReadLifecycleResult_MissingStatus_FailsClosedToEscalated()
    {
        var exit = DecompositionBindingHelper.ReadLifecycleResult(new Dictionary<string, object>
        {
            ["documentJson"] = ValidPayload,
        });
        exit.Status.Should().Be(DocumentLifecycleResult.StatusEscalated);
        exit.Outcome.Should().Be(DocumentLifecycleOutcome.ValidationExhausted.ToWire());
    }

    [Test]
    public void ReadLifecycleResult_MissingDocumentJson_DefaultsToEmptyObject()
    {
        var exit = DecompositionBindingHelper.ReadLifecycleResult(new Dictionary<string, object>
        {
            ["status"] = "rejected",
        });
        exit.Status.Should().Be("rejected");
        exit.DocumentJson.Should().Be("{}");
    }

    // ── CountSubtasks ───────────────────────────────────────────────────

    [Test]
    public void CountSubtasks_ValidPayload_CountsTasks()
        => DecompositionBindingHelper.CountSubtasks(ValidPayload).Should().Be(2);

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("{ malformed")]
    [TestCase("{}")]
    public void CountSubtasks_UnreadableOrEmpty_ReturnsZero(string body)
        => DecompositionBindingHelper.CountSubtasks(body).Should().Be(0);

    // ── BuildFailureDetail — names the reachable outcome wires + rejected ─

    [TestCase("validation-exhausted")]
    [TestCase("rounds-exhausted")]
    [TestCase("review-undecidable")]
    public void BuildFailureDetail_Escalated_NamesTheTypedOutcome(string outcome)
    {
        var detail = DecompositionBindingHelper.BuildFailureDetail(
            new DecompositionBindingHelper.LifecycleExit("escalated", outcome, null, "{}"));
        detail.Should().Contain("escalated").And.Contain(outcome);
    }

    [Test]
    public void BuildFailureDetail_Rejected_NamesTheStatus()
    {
        var detail = DecompositionBindingHelper.BuildFailureDetail(
            new DecompositionBindingHelper.LifecycleExit("rejected", null, null, "{}"));
        detail.Should().Contain("rejected");
    }

    // ── AC5/AC4 — the accepted payload is the Stories 2-15/2-16 consumer shape ──

    [Test]
    public void AcceptedPayload_SerializesToTheConsumerShape_SummaryAndSubtasksWithDependsOn()
    {
        var typed = new TypedDecomposition
        {
            Summary = "how the breakdown preserves the parent intent",
            Subtasks = new[]
            {
                new DecompositionTask { Id = "ST-1", Title = "a", Description = "x", EstimateHours = 4, Complexity = "low", DependsOn = new[] { "ST-2" } },
                new DecompositionTask { Id = "ST-2", Title = "b", Description = "y", EstimateHours = 3, Complexity = "medium" },
            },
        };

        var json = JsonSerializer.Serialize(typed, DocumentJson.Options);

        // The wire shape the Stories 2-15/2-16 consumers read (technical-note stability).
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("summary").GetString().Should().NotBeNullOrWhiteSpace();
        var subtasks = root.GetProperty("subtasks");
        subtasks.GetArrayLength().Should().Be(2);
        var first = subtasks[0];
        first.GetProperty("id").GetString().Should().Be("ST-1");
        first.GetProperty("dependsOn").EnumerateArray().Select(e => e.GetString()).Should().Contain("ST-2");

        // Round-trips through the binding helper's subtask count.
        DecompositionBindingHelper.CountSubtasks(json).Should().Be(2);
    }
}
