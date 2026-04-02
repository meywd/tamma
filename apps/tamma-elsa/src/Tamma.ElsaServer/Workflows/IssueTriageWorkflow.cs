using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Issue Triage — LLM classifies, labels, and prioritizes incoming issues.
/// Triggered by webhooks or ADL when untriaged issues are found.
///
/// Inputs: repository
/// Outputs: triagedCount
/// </summary>
public class IssueTriageWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Issue Triage";
        builder.DefinitionId = "issue-triage";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "LLM classifies, labels, and prioritizes untriaged issues";

        var stub = new Finish { Id = "Stub", Name = "Stub: Issue Triage" };
        stub.SetDisplayText("Stub: Issue Triage — TODO");
        builder.Root = stub;
    }
}
