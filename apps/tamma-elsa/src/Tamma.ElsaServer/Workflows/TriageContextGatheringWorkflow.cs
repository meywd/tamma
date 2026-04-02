using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Gathers context for triage: code usage of affected package,
/// dependency graph, CVE details, changelog, migration guide.
///
/// Inputs: repository, itemJson
/// Outputs: contextJson
/// </summary>
public class TriageContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Context Gathering";
        builder.DefinitionId = "triage-context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Gather context for triage: code usage, deps, CVE, changelog";

        var stub = new Finish { Id = "Stub", Name = "Stub: Triage Context" };
        stub.SetDisplayText("Stub: Triage Context — TODO");
        builder.Root = stub;
    }
}
