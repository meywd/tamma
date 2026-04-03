using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Deployment Pipeline — QA → UAT → Prod deployment with gates.
/// Handles releases, tags, changelog, environment promotion.
///
/// Inputs: repository, mergeSha, issueNumber, branchName
/// Outputs: deploymentResult
/// </summary>
public class DeploymentPipelineWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Deployment Pipeline";
        builder.DefinitionId = "deployment-pipeline";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Deploy through QA → UAT → Prod with gates, releases, and tags";

        var stub = new Finish { Id = "Stub", Name = "Stub: Deployment Pipeline" };
        stub.SetDisplayText("Stub: Deployment Pipeline — TODO");
        builder.Root = stub;
    }
}
