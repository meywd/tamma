using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// PO makes final triage decision based on panel review:
/// - Priority (urgent/high/normal/low)
/// - Type (bug/feature/chore/security/docs)
/// - Complexity (trivial/simple/medium/complex/epic)
/// - Automation (tamma-auto/tamma-assist/needs-human)
/// - Labels to apply
/// - Triage comment to post
///
/// Inputs: repository, itemJson, panelResultJson
/// Outputs: decisionJson
/// </summary>
public class TriagePODecisionWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage PO Decision";
        builder.DefinitionId = "triage-po-decision";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "PO makes final triage decision based on panel review";

        var stub = new Finish { Id = "Stub", Name = "Stub: PO Decision" };
        stub.SetDisplayText("Stub: PO Decision — TODO");
        builder.Root = stub;
    }
}
