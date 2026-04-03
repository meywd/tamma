using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Panel review for triage: Security Analyst, Developer, DevOps, QA
/// each assess the item from their perspective.
///
/// For security alerts: CVE impact, attack surface, breaking changes,
/// dependency chain, compatibility.
///
/// For issues: type classification, complexity estimate, scope.
///
/// Inputs: repository, itemJson, contextJson
/// Outputs: panelResultJson
/// </summary>
public class TriagePanelReviewWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Panel Review";
        builder.DefinitionId = "triage-panel-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "4-role panel reviews item for triage (security/dev/devops/qa)";

        var setDefault = new SetOutput
        {
            Id = "SetDefaultPanelResultJson",
            Name = "Set Default panelResultJson",
            OutputName = new("panelResultJson"),
            OutputValue = new(ctx => (object)"{}")
        };
        setDefault.SetDisplayText("Set Default panelResultJson");

        var stub = new Finish { Id = "Stub", Name = "Stub: Triage Panel" };
        stub.SetDisplayText("Stub: Triage Panel — TODO");

        builder.Root = new Sequence
        {
            Id = "TriagePanelReviewSequence",
            Activities = { setDefault, stub }
        };
    }
}
