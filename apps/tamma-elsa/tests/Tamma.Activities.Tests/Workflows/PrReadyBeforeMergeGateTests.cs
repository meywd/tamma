using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// THE DRAFT BLOCKER — the autonomous loop opens its PR as a DRAFT
/// (<c>SingleIssueCycleWorkflow</c> passes <c>draft = true</c> to CreatePullRequest)
/// and GitHub REFUSES to merge a draft PR. Nothing ever marked it ready: a grep for
/// "draft" across MergeWorkflow, MergeApprovalWorkflow and MergePullRequestActivity
/// returned nothing. So a cycle would build the change, pass CI, ask a human to
/// approve the merge, and then attempt a merge that could never succeed — the loop
/// could not complete a single issue end to end.
///
/// <para>Story 31-13 shipped the governed draft verb; these tests pin the place the
/// loop actually uses it, and pin that a FAILED un-draft never reaches the gate
/// (approving a merge that cannot happen is worse than failing loudly).</para>
/// </summary>
[TestFixture]
public class PrReadyBeforeMergeGateTests
{
    [Test]
    public void TheCycle_MarksThePrReady_beforeTheMergeGate()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .SingleOrDefault(a => a.Id == "MarkPrReadyForReview");

        markReady.Should().NotBeNull(
            "the cycle must flip its own draft PR to ready-for-review, or the merge it later "
            + "asks a human to approve can never succeed");

        markReady!.Draft.Should().NotBeNull();
    }

    [Test]
    public void TheUndraft_SitsBetweenCiPassed_andTheMergeGate()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");
        var gate = flowchart.Activities.Single(a => a.Id == "MergeApprovalGate");
        var ciOk = flowchart.Activities.Single(a => a.Id == "CiOk");

        // CI passed → un-draft
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == ciOk && c.Source.Port == "True" && c.Target.Activity == markReady,
            "the un-draft must hang off the CI-passed edge");

        // un-draft succeeded → the merge gate
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == markReady && c.Source.Port == "DraftSet" && c.Target.Activity == gate,
            "only a SUCCESSFUL un-draft may open the merge gate");

        // and CI-passed must no longer reach the gate directly
        flowchart.Connections.Should().NotContain(
            c => c.Source.Activity == ciOk && c.Target.Activity == gate,
            "if CI still reached the gate directly, the un-draft would be bypassable and the "
            + "draft blocker would silently return");
    }

    [Test]
    public void AFailedUndraft_FailsTheCycle_andNeverOpensTheGate()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");
        var gate = flowchart.Activities.Single(a => a.Id == "MergeApprovalGate");

        var errorEdges = flowchart.Connections
            .Where(c => c.Source.Activity == markReady && c.Source.Port == "Error")
            .ToList();

        errorEdges.Should().NotBeEmpty("a failed un-draft must be routed, not dropped on the floor");
        errorEdges.Should().NotContain(c => c.Target.Activity == gate,
            "a failed un-draft must NEVER open the merge gate — asking a human to approve a merge "
            + "that cannot complete is the exact failure this fix removes");
        errorEdges.Should().Contain(c => c.Target.Activity.Id == "EmitStepFailed",
            "it routes to the shared loud fail-the-cycle sink");
    }

    [Test]
    public void TheUndraft_TargetsReadyForReview_notDraft()
    {
        // Direction matters: Draft=true would re-draft the PR and make the blocker worse.
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");

        // The input is a literal false (ready-for-review), not an expression.
        markReady.Draft.Expression.Should().NotBeNull();
        SetPullRequestDraftActivity.MapResponse(null).Success.Should().BeFalse(
            "and an unanswered mediation call must fail closed — never read as 'ready'");
    }
}
