using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Tamma.Activities.Design;
using Tamma.Activities.Design.Models;
using Tamma.ElsaServer.Workflows.Helpers;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-13 (D5) — the tiny pre-ACCEPT delivery sub-workflow
/// (<c>DefinitionId = "design-proposal-delivery"</c>) the generic
/// <see cref="DocumentLifecycleWorkflow"/> dispatches (via its
/// <c>deliveryWorkflowDefinitionId</c> hook) BEFORE it publishes the design acceptance
/// request. It wraps <see cref="DeliverDesignProposalActivity"/> and emits the legacy
/// <c>DESIGN.PROPOSAL.GENERATED</c>/<c>DELIVERED</c> events so the design proposal is posted
/// to the issue — and audited — before the human decides, exactly as the pre-migration
/// <c>design-proposal</c> workflow did. Runs to completion (no suspend); the accept gate lives
/// in the parent lifecycle. Run-to-completion leaf (no suspend / re-entry) — allow-listed
/// in <c>ResumableStandardStructuralTests</c>.
/// </summary>
public class DesignDeliveryWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "DesignDelivery";
        builder.DefinitionId = "design-proposal-delivery";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Deliver a design proposal to the issue and emit DESIGN.PROPOSAL.GENERATED/DELIVERED before the accept gate";

        var sessionId    = builder.WithVariable<Guid>().Persisted();
        var issueId      = builder.WithVariable<string>().Persisted();
        var repository   = builder.WithVariable<string>().Persisted();
        var issueNumber  = builder.WithVariable<int>().Persisted();
        var proposalJson = builder.WithVariable<string>().Persisted();
        var tenantId     = builder.WithVariable<string>("TenantId", "").Persisted();
        var alternativeCount = builder.WithVariable<int>().Persisted();
        var deliveryResult = builder.WithVariable<DesignDeliveryResult>().Persisted();

        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                Guid.TryParse(context.GetInput<string>("sessionId"), out var sid);
                issueId.Set(context, context.GetInput<string>("issueId") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                proposalJson.Set(context, context.GetInput<string>("documentJson") ?? "{}");
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                alternativeCount.Set(context, AssessmentBindingHelper.CountAlternatives(context.GetInput<string>("documentJson")));
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        var emitGenerated = new EmitDesignEventActivity
        {
            Id = "EmitProposalGenerated", Name = "Emit Proposal Generated",
            EventType = new(DesignEvents.ProposalGenerated),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
        };
        emitGenerated.SetDisplayText("Emit Proposal Generated");

        var deliverProposal = new DeliverDesignProposalActivity
        {
            Id = "DeliverDesignProposal", Name = "Deliver Design Proposal",
            SessionId = new(ctx => sessionId.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            IssueNumber = new(ctx => issueNumber.Get(ctx)),
            ProposalJson = new(ctx => proposalJson.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Result = new(deliveryResult),
        };
        deliverProposal.SetDisplayText("Deliver Design Proposal");

        var emitDelivered = new EmitDesignEventActivity
        {
            Id = "EmitProposalDelivered", Name = "Emit Proposal Delivered",
            EventType = new(DesignEvents.ProposalDelivered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Channel = new(ctx => deliveryResult.Get(ctx)?.Channel ?? "api"),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
        };
        emitDelivered.SetDisplayText("Emit Proposal Delivered");

        builder.Root = new Sequence
        {
            Id = "DesignDeliverySequence",
            Activities = { readInputs, emitGenerated, deliverProposal, emitDelivered },
        };
    }
}
