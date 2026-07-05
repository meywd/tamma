using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Design;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 3.7 — structural verification for <see cref="DesignProposalWorkflow"/>.
///
/// Asserts the workflow:
/// 1. Builds and has DefinitionId "design-proposal".
/// 2. Threads <c>TenantId</c> so the prompt registry resolves tenant-scoped prompts
///    (resolution is tenant→system→error — never empty/plain) for role=architect /
///    action=plan-system-design.
/// 3. Generates the design proposal via <c>DispatchWorkflow("llm-call")</c> (mediated —
///    the engine holds no LLM credential, TAMMA001) rather than any in-engine provider call.
/// 4. Delivers the proposal to the reviewer via <see cref="DeliverDesignProposalActivity"/>.
/// 5. Suspends on the <see cref="WaitForDesignApprovalActivity"/> bookmark awaiting the
///    human review decision (approval gate).
/// 6. Is fail-closed: an <c>LlmCallError</c> terminal exists and a <c>FlowDecision</c> gate
///    checks LLM-call success before proceeding (never a fabricated design).
/// 7. Emits the required DESIGN.* DCB events (generated / delivered / approved / rejected /
///    failed / review timed out) via <see cref="EmitDesignEventActivity"/> nodes.
/// </summary>
[TestFixture]
public class DesignProposalWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow());
        act.Should().NotThrow("DesignProposalWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void Workflow_HasCorrectDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow());
        builder.Object.DefinitionId.Should().Be("design-proposal");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow());
        builder.Object.Variables
            .Any(v => v.Name == "TenantId")
            .Should().BeTrue(
                "the workflow must thread TenantId so llm-call resolves tenant-scoped prompts " +
                "(tenant→system→error) for architect/plan-system-design");
    }

    [Test]
    public void Workflow_DispatchesLlmCallForProposalGeneration()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "GenerateProposalLlm",
                "the design proposal must be generated via the mediated llm-call (engine holds no LLM credential)");
    }

    [Test]
    public void Workflow_DeliversProposal()
    {
        Flowchart().Activities
            .OfType<DeliverDesignProposalActivity>()
            .Should().ContainSingle(a => a.Id == "DeliverDesignProposal",
                "the workflow must deliver the design proposal to the reviewer");
    }

    [Test]
    public void Workflow_SuspendsOnApprovalBookmark()
    {
        Flowchart().Activities
            .OfType<WaitForDesignApprovalActivity>()
            .Should().ContainSingle(a => a.Id == "WaitForApproval",
                "the workflow must suspend on the WaitForDesignApprovalActivity bookmark " +
                "awaiting the human review decision");
    }

    [Test]
    public void Workflow_HasFailClosedErrorTerminal()
    {
        Flowchart().Activities
            .OfType<Finish>()
            .Should().Contain(f => f.Id == "LlmCallError",
                "a fail-closed LlmCallError terminal must exist — an LLM-call failure routes there, " +
                "never proceeding with a fabricated design");
    }

    [Test]
    public void Workflow_HasSuccessGateForGeneration()
    {
        Flowchart().Activities.OfType<FlowDecision>().Select(d => d.Id)
            .Should().Contain("ProposalLlmOk",
                "proposal delivery must be gated behind a ProposalLlmOk decision (fail-closed)");
    }

    [Test]
    public void Workflow_EmitsRequiredDesignEvents()
    {
        var emitIds = Flowchart().Activities
            .OfType<EmitDesignEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitProposalGenerated",
            "must emit DESIGN.PROPOSAL.GENERATED when the proposal is produced");
        emitIds.Should().Contain("EmitProposalDelivered",
            "must emit DESIGN.PROPOSAL.DELIVERED when the proposal is delivered");
        emitIds.Should().Contain("EmitProposalApproved",
            "must emit DESIGN.PROPOSAL.APPROVED when the reviewer approves");
        emitIds.Should().Contain("EmitProposalRejected",
            "must emit DESIGN.PROPOSAL.REJECTED when the reviewer rejects");
        emitIds.Should().Contain("EmitProposalFailed",
            "must emit a LOUD DESIGN.PROPOSAL.FAILED on the fail-closed path");
        emitIds.Should().Contain("EmitReviewTimedOut",
            "must emit a LOUD DESIGN.REVIEW.TIMED_OUT when the review SLA expires");
    }

    [Test]
    public void Workflow_ApprovalGate_HasApprovedRejectedTimeoutBranches()
    {
        var flow = Flowchart();

        var outcomes = flow.Connections
            .Where(c => c.Source.Activity.Id == "WaitForApproval")
            .Select(c => c.Source.Port)
            .ToList();

        outcomes.Should().Contain("Approved", "the gate must branch on an approve decision");
        outcomes.Should().Contain("Rejected", "the gate must branch on a reject decision");
        outcomes.Should().Contain("Timeout", "the gate must branch on the durable review-SLA timeout");
    }
}
