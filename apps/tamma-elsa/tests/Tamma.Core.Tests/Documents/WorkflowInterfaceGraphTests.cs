using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// The graph-walk build test over the static workflow interface declarations
/// (Story 39-2 AC4; Design Decision D6). Same posture as
/// <c>ContractBindingTests.KnownContractViolations</c>: a ratchet allowlist of
/// not-yet-implemented type keys that may only shrink as 39-3/39-4 land.
/// </summary>
[TestFixture]
public class WorkflowInterfaceGraphTests
{
    private static readonly Regex Kebab = new("^[a-z0-9]+(-[a-z0-9]+)*$");

    /// <summary>
    /// Type keys whose <see cref="IDocumentType"/> implementation is still pending
    /// (39-3/39-4). Starts as ALL 10 vocabulary keys; may only SHRINK. When a key
    /// becomes registered, it must be removed from this set — a stale entry (a
    /// pending key that is now registered) fails <see cref="Pending_entry_is_not_already_registered"/>.
    /// </summary>
    private static readonly HashSet<DocumentTypeKey> PendingImplementations = new()
    {
        // EMPTY — Story 39-3 registered Findings/AmbiguityAssessment/Clarification/
        // Decomposition and Story 39-4 registered the remaining six (Plan, Design,
        // Review, TriageDecision, Diagnosis, TestSpec). The vocabulary is complete, so
        // NO type key is pending; a re-added entry for a registered key fails
        // Pending_entry_is_not_already_registered by design.
    };

    [Test]
    public void Declared_edge_count_is_pinned()
    {
        // Adding/removing a workflow declaration is a conscious edit. The D6 seed
        // maps the README document-type table onto real Elsa DefinitionIds
        // (plan-generation + task-creation both produce 'plan'; plan-review /
        // task-review / code-review all produce 'review'; blocker-diagnosis +
        // debugging both produce 'diagnosis').
        // Story 39-15 — 15 → 16: added the triage-context-gathering → findings edge (the split
        // Findings binding); triage-po-decision now consumes [findings]/produces triage-decision.
        DocumentTypeRegistry.WorkflowInterfaces.Should().HaveCount(16);
    }

    [Test]
    public void Every_workflow_definition_id_is_non_empty_kebab()
    {
        foreach (var iface in DocumentTypeRegistry.WorkflowInterfaces)
        {
            iface.WorkflowDefinitionId.Should().NotBeNullOrWhiteSpace();
            Kebab.IsMatch(iface.WorkflowDefinitionId)
                .Should().BeTrue($"'{iface.WorkflowDefinitionId}' must be kebab-case");
        }
    }

    [Test]
    public void Workflow_definition_ids_are_unique()
    {
        var ids = DocumentTypeRegistry.WorkflowInterfaces.Select(i => i.WorkflowDefinitionId).ToArray();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Every_declared_produces_key_is_registered_or_pending()
    {
        foreach (var iface in DocumentTypeRegistry.WorkflowInterfaces)
        {
            if (iface.Produces is not { } produced)
                continue;

            var isRegistered = IsRegistered(produced);
            var isPending = PendingImplementations.Contains(produced);

            (isRegistered || isPending).Should().BeTrue(
                $"workflow '{iface.WorkflowDefinitionId}' produces '{produced.ToWire()}', " +
                "which must either have a registered implementation or be listed in PendingImplementations");
        }
    }

    [Test]
    public void Pending_entry_is_not_already_registered()
    {
        // Ratchet: once a key is registered by 39-3/39-4, its stale PendingImplementations
        // entry must be removed. A pending-yet-registered key fails here.
        foreach (var key in PendingImplementations)
        {
            IsRegistered(key).Should().BeFalse(
                $"'{key.ToWire()}' is registered now — remove it from PendingImplementations");
        }
    }

    [Test]
    public void Seeded_declarations_are_provisional_except_reconciled_bindings()
    {
        // Story 39-12/39-13 (D9): edges backed by a real document-lifecycle binding are flipped
        // non-provisional — issue-decomposition (39-12) plus the four assessment-family edges
        // (research/ambiguity-scoring/clarifying-questions/design-proposal, 39-13). Every other
        // edge stays provisional until its own migration (39-14/15) reconciles it.
        var reconciled = new[]
        {
            "issue-decomposition",
            "research",
            "ambiguity-scoring",
            "clarifying-questions",
            "design-proposal",
            // Story 39-14 — the planning family: plan-generation (consumes decomposition,
            // produces plan) and plan-review (reader) are now backed by real bindings.
            "plan-generation",
            "plan-review",
            // Story 39-15 — the creation family: task-creation (consumes plan, produces plan)
            // and test-case-creation (consumes plan, produces test-spec) are now real bindings.
            "task-creation",
            "test-case-creation",
            // Story 39-15 — the debug-diagnosis binding produces a typed Diagnosis (real binding).
            "debug-diagnosis",
            // Story 39-15 — the triage family: triage-context-gathering (produces findings) and
            // triage-po-decision (consumes [findings], produces triage-decision) are now real bindings.
            "triage-context-gathering",
            "triage-po-decision",
        };

        DocumentTypeRegistry.WorkflowInterfaces
            .Where(i => !reconciled.Contains(i.WorkflowDefinitionId))
            .Should().OnlyContain(i => i.Provisional);

        DocumentTypeRegistry.WorkflowInterfaces
            .Where(i => reconciled.Contains(i.WorkflowDefinitionId))
            .Should().OnlyContain(i => !i.Provisional,
                "edges backed by a real document-lifecycle binding (39-12/39-13 D9) are non-provisional");
    }

    private static bool IsRegistered(DocumentTypeKey key)
    {
        try
        {
            DocumentTypeRegistry.Resolve(key);
            return true;
        }
        catch (TammaError)
        {
            return false;
        }
    }
}
