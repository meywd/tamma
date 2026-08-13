using Elsa.Workflows;
using Elsa.Workflows.Memory;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// 2026-08-13 (engine-driven E2E) — the variable-persistence seam every CLR
/// workflow in this assembly uses.
///
/// <para><b>Why this exists.</b> An Elsa 3.5 <see cref="Variable"/> with no
/// explicit storage driver is EPHEMERAL: <c>VariablePersistenceManager</c>
/// only persists variables that carry a persistent
/// <see cref="Variable.StorageDriverType"/>, so every parent-workflow variable
/// silently reset to its default across ANY suspend/resume boundary — a
/// sub-workflow dispatch with <c>WaitForCompletion</c>, a document-decision
/// bookmark, a CI wait. The engine-driven E2E surfaced it as
/// <c>document-lifecycle</c> faulting with "empty state json" right after its
/// produce dispatch resumed (and, earlier, as post-resume dispatches carrying
/// empty <c>repository</c> into the context-scan stores). Every long-running
/// workflow here suspends, so every declared variable must be
/// workflow-storage backed.</para>
///
/// <para>Direct property assignment rather than Elsa's
/// <c>WithWorkflowStorage()</c> extension: the workflow STRUCTURE tests build
/// definitions through a mocked <c>IWorkflowBuilder</c>, and this seam stays
/// null-tolerant so a harness that answers <c>null</c> for an unconfigured
/// overload fails at the assertion that cares, not inside variable
/// plumbing.</para>
/// </summary>
public static class VariablePersistenceExtensions
{
    public static Variable<T> Persisted<T>(this Variable<T> variable)
    {
        if (variable is not null)
        {
            variable.StorageDriverType = typeof(WorkflowInstanceStorageDriver);
        }
        return variable!;
    }
}
