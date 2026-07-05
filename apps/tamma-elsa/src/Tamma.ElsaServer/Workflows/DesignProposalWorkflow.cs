using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities;
using Tamma.Activities.Design;
using Tamma.Activities.Design.Models;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 3.7 — Design Proposal sub-workflow. Given a complex requirement that needs a
/// technical design it uses the LLM (via the MEDIATED <c>llm-call</c> path — the engine
/// holds no LLM credential, TAMMA001) to generate a design PROPOSAL (summary + multiple
/// alternatives with trade-off analysis + constraint evaluation), DELIVERS it to the issue,
/// SUSPENDS on a bookmark awaiting a human approve/reject review decision, then RESUMES (via
/// the secure <c>DesignResumeEndpoint</c>) and finalises — approved designs hand off to
/// implementation, rejected designs capture the feedback.
///
/// Flow:
///   1. Read inputs (issue/requirement + constraints + tenantId; mint a session id if none)
///   2. Generate the design proposal via DispatchWorkflow("llm-call")
///      role=architect / action=plan-system-design
///   3. Deliver the proposal to the issue (mediated git seam) — emit DESIGN.PROPOSAL.DELIVERED
///   4. Wait for the review decision (bookmark, durable SLA timeout)
///   5a. On approve: emit DESIGN.PROPOSAL.APPROVED, set outputs (proceed to implementation)
///   5b. On reject:  emit DESIGN.PROPOSAL.REJECTED (feedback captured), set outputs
///   5c. On timeout: emit DESIGN.REVIEW.TIMED_OUT (LOUD), set outputs
///
/// Reuses the <see cref="ClarifyingQuestionsWorkflow"/> / <see cref="AssessmentWorkflow"/>
/// skeleton (llm-call → deliver → bookmark-wait → resume, fail-closed gates + error
/// terminal).
///
/// Fail-closed: if the generation <c>llm-call</c> returns success=false, or the JSON
/// response cannot be parsed into a design with a load-bearing summary, the workflow emits a
/// LOUD <c>DESIGN.PROPOSAL.FAILED</c> event and routes to the LlmCallError terminal — it
/// NEVER proceeds with a fabricated design a reviewer would then approve. Prompt resolution
/// is tenant→system→error (the <c>llm-call</c> registry never falls back to an empty/plain
/// prompt).
///
/// DESIGN.* DCB events (AGGREGATE.ACTION.STATUS) are emitted at every transition so the
/// design decision is fully auditable and feeds the Epic-32 learning loop (Story-3.7 AC
/// "System tracks design decisions and maintains decision audit trail" + "Proposals are
/// versioned and stored for future reference and learning").
/// </summary>
public class DesignProposalWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "DesignProposal";
        builder.DefinitionId = "design-proposal";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Generate a reviewed technical design proposal for a complex requirement";

        // ── Workflow variables ──────────────────────────────────────────
        var sessionId    = builder.WithVariable<Guid>();
        var issueId      = builder.WithVariable<string>();
        var requirement  = builder.WithVariable<string>();
        var repository   = builder.WithVariable<string>();
        var issueNumber  = builder.WithVariable<int>();
        var constraints  = builder.WithVariable<string>();
        var conventions  = builder.WithVariable<string>();
        var tenantId     = builder.WithVariable<string>("TenantId", "");

        var proposalJson    = builder.WithVariable<string>();
        var alternativeCount = builder.WithVariable<int>();
        var feedback        = builder.WithVariable<string>();

        // llm-call result container
        var proposalLlm = builder.WithVariable<IDictionary<string, object>?>();

        // Success flag (fail-closed guard)
        var proposalLlmOk = builder.WithVariable<bool>();

        // Activity output capture
        var deliveryResult = builder.WithVariable<DesignDeliveryResult>();
        var waitApproved   = builder.WithVariable<bool>();
        var waitFeedback   = builder.WithVariable<string>();
        var waitTimedOut   = builder.WithVariable<bool>();
        var proposalOutput = builder.WithVariable<DesignProposal>();

        // Output variables (readable by a parent workflow)
        var outputStatus       = builder.WithVariable<string>();
        var outputProposalJson = builder.WithVariable<string>();
        var outputApproved     = builder.WithVariable<bool>();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs",
            Name = "Read Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                var sid = context.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();

                issueId.Set(context, context.GetInput<string>("issueId") ?? string.Empty);
                requirement.Set(context, context.GetInput<string>("requirement") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                constraints.Set(context, context.GetInput<string>("constraints") ?? string.Empty);
                conventions.Set(context, context.GetInput<string>("conventions") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Generate the design proposal via llm-call ──────────
        var generateProposalLlm = new DispatchWorkflow
        {
            Id = "GenerateProposalLlm",
            Name = "Generate Design Proposal (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"]     = AgentRole.Architect.ToWire(),
                ["action"]   = AgentAction.PlanSystemDesign.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = constraints.Get(ctx) ?? "",
                    ["repository"]      = repository.Get(ctx) ?? "",
                    ["conventions"]     = conventions.Get(ctx) ?? "",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(proposalLlm),
        };
        generateProposalLlm.SetDisplayText("Generate Design Proposal (LLM)");

        // Parse llm-call response into a DesignProposal; set proposalLlmOk (fail-closed).
        var parseProposal = new SetVariable
        {
            Id = "ParseProposal",
            Name = "Parse Proposal",
            Variable = proposalJson,
            Value = new(ctx =>
            {
                var result = proposalLlm.Get(ctx);
                if (!ReadSuccessFlag(result))
                {
                    proposalLlmOk.Set(ctx, false);
                    return "{}";
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                var parsed = DesignParsing.ParseProposal(text);
                if (parsed is null)
                {
                    // Fail-closed — no fabricated / empty design proposal.
                    proposalLlmOk.Set(ctx, false);
                    return "{}";
                }

                proposalLlmOk.Set(ctx, true);
                proposalOutput.Set(ctx, parsed);
                alternativeCount.Set(ctx, parsed.Alternatives.Count);
                return JsonSerializer.Serialize(parsed);
            })
        };
        parseProposal.SetDisplayText("Parse Proposal");

        var proposalSuccessCheck = new FlowDecision(ctx => proposalLlmOk.Get(ctx))
        { Id = "ProposalLlmOk", Name = "Proposal LLM OK?" };
        proposalSuccessCheck.SetDisplayText("Proposal LLM OK?");

        // ── Step 3: Emit GENERATED + deliver + emit DELIVERED ──────────
        var emitProposalGenerated = new EmitDesignEventActivity
        {
            Id = "EmitProposalGenerated",
            Name = "Emit Proposal Generated",
            EventType = new(DesignEvents.ProposalGenerated),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
        };
        emitProposalGenerated.SetDisplayText("Emit Proposal Generated");

        var deliverProposal = new DeliverDesignProposalActivity
        {
            Id = "DeliverDesignProposal",
            Name = "Deliver Design Proposal",
            SessionId = new(ctx => sessionId.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            IssueNumber = new(ctx => issueNumber.Get(ctx)),
            ProposalJson = new(ctx => proposalJson.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Result = new(deliveryResult),
        };
        deliverProposal.SetDisplayText("Deliver Design Proposal");

        var emitProposalDelivered = new EmitDesignEventActivity
        {
            Id = "EmitProposalDelivered",
            Name = "Emit Proposal Delivered",
            EventType = new(DesignEvents.ProposalDelivered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Channel = new(ctx => deliveryResult.Get(ctx)?.Channel ?? "api"),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
        };
        emitProposalDelivered.SetDisplayText("Emit Proposal Delivered");

        // ── Step 4: Wait for the review decision (bookmark + durable SLA) ─
        var waitForApproval = new WaitForDesignApprovalActivity
        {
            Id = "WaitForApproval",
            Name = "Wait For Design Approval",
            SessionId = new(ctx => sessionId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Approved = new(waitApproved),
            Feedback = new(waitFeedback),
            TimedOut = new(waitTimedOut),
        };
        waitForApproval.SetDisplayText("Wait For Design Approval");

        // ── Step 5a: Approved path ─────────────────────────────────────
        var storeApproved = new SetVariable
        {
            Id = "StoreApproved",
            Name = "Store Approved",
            Variable = feedback,
            Value = new(ctx => waitFeedback.Get(ctx) ?? string.Empty)
        };
        storeApproved.SetDisplayText("Store Approved");

        var emitProposalApproved = new EmitDesignEventActivity
        {
            Id = "EmitProposalApproved",
            Name = "Emit Proposal Approved",
            EventType = new(DesignEvents.ProposalApproved),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
            Detail = new(ctx => feedback.Get(ctx)),
        };
        emitProposalApproved.SetDisplayText("Emit Proposal Approved");

        var setApprovedResult = new SetVariable
        {
            Id = "SetApprovedResult",
            Name = "Set Approved Result",
            Variable = outputStatus,
            Value = new(ctx =>
            {
                outputProposalJson.Set(ctx, proposalJson.Get(ctx) ?? "{}");
                outputApproved.Set(ctx, true);
                return "approved";
            })
        };
        setApprovedResult.SetDisplayText("Set Approved Result");

        var exposeApprovedOutput = new Sequence
        {
            Id = "ExposeApprovedOutput",
            Name = "Expose Approved Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionIdApproved", Name = "Output Session Id (Approved)", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id (Approved)"),
                WithLabel(new SetOutput { Id = "OutputStatusApproved", Name = "Output Status (Approved)", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status (Approved)"),
                WithLabel(new SetOutput { Id = "OutputProposalApproved", Name = "Output Proposal (Approved)", OutputName = new("designProposal"), OutputValue = new(ctx => (object)(outputProposalJson.Get(ctx) ?? "{}")) }, "Output Proposal (Approved)"),
                WithLabel(new SetOutput { Id = "OutputApprovedApproved", Name = "Output Approved (Approved)", OutputName = new("approved"), OutputValue = new(ctx => (object)outputApproved.Get(ctx)) }, "Output Approved (Approved)"),
            }
        };
        exposeApprovedOutput.SetDisplayText("Expose Approved Output");

        // ── Step 5b: Rejected path ─────────────────────────────────────
        var storeRejected = new SetVariable
        {
            Id = "StoreRejected",
            Name = "Store Rejected",
            Variable = feedback,
            Value = new(ctx => waitFeedback.Get(ctx) ?? string.Empty)
        };
        storeRejected.SetDisplayText("Store Rejected");

        var emitProposalRejected = new EmitDesignEventActivity
        {
            Id = "EmitProposalRejected",
            Name = "Emit Proposal Rejected",
            EventType = new(DesignEvents.ProposalRejected),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
            Detail = new(ctx => feedback.Get(ctx)),
        };
        emitProposalRejected.SetDisplayText("Emit Proposal Rejected");

        var setRejectedResult = new SetVariable
        {
            Id = "SetRejectedResult",
            Name = "Set Rejected Result",
            Variable = outputStatus,
            Value = new(ctx =>
            {
                outputProposalJson.Set(ctx, proposalJson.Get(ctx) ?? "{}");
                outputApproved.Set(ctx, false);
                return "rejected";
            })
        };
        setRejectedResult.SetDisplayText("Set Rejected Result");

        var exposeRejectedOutput = new Sequence
        {
            Id = "ExposeRejectedOutput",
            Name = "Expose Rejected Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionIdRejected", Name = "Output Session Id (Rejected)", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id (Rejected)"),
                WithLabel(new SetOutput { Id = "OutputStatusRejected", Name = "Output Status (Rejected)", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status (Rejected)"),
                WithLabel(new SetOutput { Id = "OutputProposalRejected", Name = "Output Proposal (Rejected)", OutputName = new("designProposal"), OutputValue = new(ctx => (object)(outputProposalJson.Get(ctx) ?? "{}")) }, "Output Proposal (Rejected)"),
                WithLabel(new SetOutput { Id = "OutputApprovedRejected", Name = "Output Approved (Rejected)", OutputName = new("approved"), OutputValue = new(ctx => (object)outputApproved.Get(ctx)) }, "Output Approved (Rejected)"),
            }
        };
        exposeRejectedOutput.SetDisplayText("Expose Rejected Output");

        // ── Step 5c: Timeout path ──────────────────────────────────────
        var setTimeoutResult = new SetVariable
        {
            Id = "SetTimeoutResult",
            Name = "Set Timeout Result",
            Variable = outputStatus,
            Value = new(ctx =>
            {
                outputProposalJson.Set(ctx, proposalJson.Get(ctx) ?? "{}");
                outputApproved.Set(ctx, false);
                return "timed_out";
            })
        };
        setTimeoutResult.SetDisplayText("Set Timeout Result");

        var emitReviewTimedOut = new EmitDesignEventActivity
        {
            Id = "EmitReviewTimedOut",
            Name = "Emit Review Timed Out",
            EventType = new(DesignEvents.ReviewTimedOut),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Design review SLA expired with no reviewer decision"),
        };
        emitReviewTimedOut.SetDisplayText("Emit Review Timed Out");

        var exposeTimeoutOutput = new Sequence
        {
            Id = "ExposeTimeoutOutput",
            Name = "Expose Timeout Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionIdTimeout", Name = "Output Session Id (Timeout)", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputStatusTimeout", Name = "Output Status (Timeout)", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputApprovedTimeout", Name = "Output Approved (Timeout)", OutputName = new("approved"), OutputValue = new(ctx => (object)outputApproved.Get(ctx)) }, "Output Approved (Timeout)"),
            }
        };
        exposeTimeoutOutput.SetDisplayText("Expose Timeout Output");

        // ── Fail-closed error terminal (LOUD event + Finish) ───────────
        var emitProposalFailed = new EmitDesignEventActivity
        {
            Id = "EmitProposalFailed",
            Name = "Emit Proposal Failed",
            EventType = new(DesignEvents.ProposalFailed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("llm-call for design-proposal generation failed or returned unparseable output"),
        };
        emitProposalFailed.SetDisplayText("Emit Proposal Failed");

        var llmCallError = new Finish
        {
            Id = "LlmCallError",
            Name = "LLM Call Error"
        };
        llmCallError.SetDisplayText("LLM Call Error");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "DesignProposalFlowchart",
            Name = "Design Proposal Flowchart",
            Activities =
            {
                readInputs,
                generateProposalLlm,
                parseProposal,
                proposalSuccessCheck,
                emitProposalGenerated,
                deliverProposal,
                emitProposalDelivered,
                waitForApproval,

                // Approved path
                storeApproved,
                emitProposalApproved,
                setApprovedResult,
                exposeApprovedOutput,

                // Rejected path
                storeRejected,
                emitProposalRejected,
                setRejectedResult,
                exposeRejectedOutput,

                // Timeout path
                setTimeoutResult,
                emitReviewTimedOut,
                exposeTimeoutOutput,

                // Fail-closed error terminal
                emitProposalFailed,
                llmCallError
            },
            Connections =
            {
                new(readInputs, generateProposalLlm),
                new(generateProposalLlm, parseProposal),
                new(parseProposal, proposalSuccessCheck),
                new(new FlowEndpoint(proposalSuccessCheck, "True"),  new FlowEndpoint(emitProposalGenerated)),
                new(new FlowEndpoint(proposalSuccessCheck, "False"), new FlowEndpoint(emitProposalFailed)),
                new(emitProposalFailed, llmCallError),

                new(emitProposalGenerated, deliverProposal),
                new(deliverProposal, emitProposalDelivered),
                new(emitProposalDelivered, waitForApproval),

                // Approved path
                new(new FlowEndpoint(waitForApproval, "Approved"), new FlowEndpoint(storeApproved)),
                new(storeApproved, emitProposalApproved),
                new(emitProposalApproved, setApprovedResult),
                new(setApprovedResult, exposeApprovedOutput),

                // Rejected path
                new(new FlowEndpoint(waitForApproval, "Rejected"), new FlowEndpoint(storeRejected)),
                new(storeRejected, emitProposalRejected),
                new(emitProposalRejected, setRejectedResult),
                new(setRejectedResult, exposeRejectedOutput),

                // Timeout path
                new(new FlowEndpoint(waitForApproval, "Timeout"), new FlowEndpoint(setTimeoutResult)),
                new(setTimeoutResult, emitReviewTimedOut),
                new(emitReviewTimedOut, exposeTimeoutOutput)
            }
        };
    }

    /// <summary>
    /// Read the <c>success</c> flag from a dispatched workflow's Result dictionary. Returns
    /// <c>false</c> if the dictionary is null, the key is absent, or the value is falsy —
    /// fail-closed by design. Uses the tolerant <see cref="ResumeInput.AsBool"/> read (boxed
    /// bool / string / JsonElement).
    /// </summary>
    internal static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return ResumeInput.AsBool(s);
    }
}
