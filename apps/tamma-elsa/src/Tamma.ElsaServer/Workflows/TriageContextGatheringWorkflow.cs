using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
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

        var setDefault = new SetOutput
        {
            Id = "SetDefaultContextJson",
            Name = "Set Default contextJson",
            OutputName = new("contextJson"),
            OutputValue = new(ctx => (object)"{}")
        };
        setDefault.SetDisplayText("Set Default contextJson");

        var stub = new Finish { Id = "Stub", Name = "Stub: Triage Context" };
        stub.SetDisplayText("Stub: Triage Context — TODO");

        builder.Root = new Sequence
        {
            Id = "TriageContextGatheringSequence",
            Activities = { setDefault, stub }
        };
    }
}
