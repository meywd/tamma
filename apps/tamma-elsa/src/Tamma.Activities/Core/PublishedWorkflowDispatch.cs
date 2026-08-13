using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Runtime.Requests;

namespace Tamma.Activities.Core;

/// <summary>
/// 2026-08-13 (found by the engine-driven E2E) — the ONE correct way to build a
/// <see cref="DispatchWorkflowDefinitionRequest"/> from a workflow DEFINITION id.
///
/// <para><b>The defect this exists to prevent:</b> the request's constructor
/// parameter is <c>definitionVersionId</c> — the VERSION id (e.g.
/// <c>"AdlOrchestratorWorkflow:v1"</c>), NOT the definition id
/// (<c>"adl-orchestrator"</c>). Every background dispatch site in the codebase
/// passed the definition id, so the queue handler answered
/// <c>WorkflowGraphNotFoundException</c> for EVERY activity-driven dispatch —
/// the ADL orchestrator could never start a single-issue cycle, the loop could
/// never restart itself, triage never dispatched, and the analytics rollup
/// never ran. It ships latent because the dispatchers are fire-and-forget by
/// design (a dispatch failure is logged and swallowed so the loop survives) —
/// nothing red ever surfaced until the engine-driven E2E asserted the cycle
/// actually runs.</para>
/// </summary>
public static class PublishedWorkflowDispatch
{
    /// <summary>
    /// Resolve the PUBLISHED version id for <paramref name="definitionId"/> —
    /// the value <see cref="DispatchWorkflowDefinitionRequest"/>'s constructor
    /// actually requires. Throws when no published version exists — callers
    /// keep their existing fire-and-forget catch posture.
    /// </summary>
    public static async Task<string> ResolvePublishedVersionIdAsync(
        IWorkflowDefinitionService definitionService,
        string definitionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definitionService);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);

        var definition = await definitionService
            .FindWorkflowDefinitionAsync(definitionId, VersionOptions.Published, ct)
            .ConfigureAwait(false);

        if (definition is null)
        {
            throw new InvalidOperationException(
                $"No PUBLISHED workflow definition found for definition id '{definitionId}' — "
                + "cannot dispatch (DispatchWorkflowDefinitionRequest requires the VERSION id).");
        }

        return definition.Id;
    }
}
