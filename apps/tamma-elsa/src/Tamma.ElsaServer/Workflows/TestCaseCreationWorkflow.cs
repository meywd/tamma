using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Test Case Creation — generates test cases from task plans.
/// Commits failing test files to the PR branch before TDD starts.
///
/// Inputs: repository, branchName, tasksJson, contextIds
/// Outputs: testCasesJson
/// </summary>
public class TestCaseCreationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Test Case Creation";
        builder.DefinitionId = "test-case-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Generate test cases from task plans and commit to PR branch";

        var stub = new Finish { Id = "Stub", Name = "Stub: Test Case Creation" };
        stub.SetDisplayText("Stub: Test Case Creation — TODO");
        builder.Root = stub;
    }
}
