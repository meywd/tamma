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
        DocumentTypeKey.Findings,
        DocumentTypeKey.AmbiguityAssessment,
        DocumentTypeKey.Clarification,
        DocumentTypeKey.Decomposition,
        DocumentTypeKey.Plan,
        DocumentTypeKey.Design,
        DocumentTypeKey.Review,
        DocumentTypeKey.TriageDecision,
        DocumentTypeKey.Diagnosis,
        DocumentTypeKey.TestSpec,
    };

    [Test]
    public void Declared_edge_count_is_pinned()
    {
        // Adding/removing a workflow declaration is a conscious edit. The D6 seed
        // maps the README document-type table onto real Elsa DefinitionIds
        // (plan-generation + task-creation both produce 'plan'; plan-review /
        // task-review / code-review all produce 'review'; blocker-diagnosis +
        // debugging both produce 'diagnosis').
        DocumentTypeRegistry.WorkflowInterfaces.Should().HaveCount(14);
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
    public void All_seeded_declarations_are_provisional_until_the_39_1_audit_lands()
    {
        DocumentTypeRegistry.WorkflowInterfaces.Should().OnlyContain(i => i.Provisional);
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
