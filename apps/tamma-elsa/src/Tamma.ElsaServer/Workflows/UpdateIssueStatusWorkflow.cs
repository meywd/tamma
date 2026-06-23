using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Update Issue Status — post a status comment on a GitHub issue, manage labels,
/// and (when a merged PR is supplied) compose a PR-linked close comment, keeping
/// the issue a "living log" of the autonomous cycle.
///
/// <para>Story 2.10 build-out: the activity no longer <b>swallows</b> a failed
/// status update into a silent success. The workflow is now a real flowchart with
/// an EXPLICIT failure edge: on a genuine callback failure the activity surfaces a
/// <c>Failed</c> outcome that emits <c>ISSUE_STATUS.UPDATED.FAILED</c> (durable
/// DCB drain) and sets <c>success=false</c>; success emits
/// <c>ISSUE_STATUS.UPDATED.SUCCESS</c>. No <c>Failed</c> edge ever falls through
/// to the success outputs — no false success.</para>
///
/// Flow:
///   ReadInputs → UpdateIssue
///     ├─ Updated → EmitSuccess → Success Outputs → Finish
///     └─ Failed  → Failure Outputs (success=false) → EmitFailed → Finish
///
/// Inputs: repository, issueNumber, message, addLabels?, removeLabels?,
///         tenantId?, prNumber?, prUrl?.
/// </summary>
public class UpdateIssueStatusWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Update Issue Status";
        builder.DefinitionId = "update-issue-status";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Update a GitHub issue (status comment, labels, PR-linked close) with an explicit failure path and durable DCB events";

        // ================================================================
        // Variables
        // ================================================================
        var repositoryVar = builder.WithVariable<string>("Repository", "");
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0);
        var messageVar = builder.WithVariable<string>("Message", "");
        var tenantIdVar = builder.WithVariable<string>("TenantId", "");
        var prNumberVar = builder.WithVariable<int>("PrNumber", 0);
        var prUrlVar = builder.WithVariable<string>("PrUrl", "");
        var addLabelsVar = builder.WithVariable<string[]>("AddLabels", Array.Empty<string>());
        var removeLabelsVar = builder.WithVariable<string[]>("RemoveLabels", Array.Empty<string>());

        var updatedVar = builder.WithVariable<bool>("Updated", false);
        var degradedVar = builder.WithVariable<bool>("Degraded", false);
        var errorCodeVar = builder.WithVariable<string>("ErrorCode", "");
        var errorVar = builder.WithVariable<string>("Error", "");
        var startedAtTicksVar = builder.WithVariable<long>("StartedAtTicks", 0);

        // ================================================================
        // 1. Read inputs
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repositoryVar,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumberVar.Set(ctx, ctx.GetInput<int>("issueNumber"));
                messageVar.Set(ctx, ctx.GetInput<string>("message") ?? "");
                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                prNumberVar.Set(ctx, ctx.GetInput<int?>("prNumber") ?? 0);
                prUrlVar.Set(ctx, ctx.GetInput<string>("prUrl") ?? "");
                addLabelsVar.Set(ctx, ctx.GetInput<string[]>("addLabels") ?? Array.Empty<string>());
                removeLabelsVar.Set(ctx, ctx.GetInput<string[]>("removeLabels") ?? Array.Empty<string>());
                startedAtTicksVar.Set(ctx, DateTime.UtcNow.Ticks);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Update the issue (outcome-bearing — Updated / Failed)
        // ================================================================
        var updateIssue = new UpdateIssueStatusActivity
        {
            Id = "UpdateIssue", Name = "Update Issue",
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Message = new Input<string>(ctx => messageVar.Get(ctx)),
            AddLabels = new Input<string[]?>(ctx => NullIfEmpty(addLabelsVar.Get(ctx))),
            RemoveLabels = new Input<string[]?>(ctx => NullIfEmpty(removeLabelsVar.Get(ctx))),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            PrUrl = new Input<string?>(ctx => prUrlVar.Get(ctx)),
            Updated = new Output<bool>(updatedVar),
            Degraded = new Output<bool>(degradedVar),
            ErrorCode = new Output<string?>(errorCodeVar),
            Error = new Output<string?>(errorVar),
        };
        updateIssue.SetDisplayText("Update Issue");

        // ================================================================
        // 3. Success path — emit ISSUE_STATUS.UPDATED.SUCCESS + outputs
        // ================================================================
        var emitSuccess = new EmitIssueStatusEventActivity
        {
            Id = "EmitSuccess", Name = "Emit ISSUE_STATUS.UPDATED.SUCCESS",
            EventType = new Input<string>(_ => IssueStatusEvents.UpdatedSuccess),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildSuccessData(
                messageVar.Get(ctx), addLabelsVar.Get(ctx), removeLabelsVar.Get(ctx),
                prNumberVar.Get(ctx), degradedVar.Get(ctx),
                ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitSuccess.SetDisplayText("Emit ISSUE_STATUS.UPDATED.SUCCESS");

        var successOutputs = new Sequence
        {
            Id = "SuccessOutputs", Name = "Success Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutDegraded", OutputName = new("degraded"), OutputValue = new(ctx => (object)degradedVar.Get(ctx)) }, "Output degraded"),
                WithLabel(new SetOutput { Id = "OutIssueNumber", OutputName = new("issueNumber"), OutputValue = new(ctx => (object)issueNumberVar.Get(ctx)) }, "Output issueNumber"),
            }
        };
        successOutputs.SetDisplayText("Success Outputs");

        // ================================================================
        // 4. Failure path — success=false, emit ISSUE_STATUS.UPDATED.FAILED
        // ================================================================
        var failureOutputs = new Sequence
        {
            Id = "FailureOutputs", Name = "Failure Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutFailSuccess", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success=false"),
                WithLabel(new SetOutput { Id = "OutFailErrorCode", OutputName = new("errorCode"), OutputValue = new(ctx => (object)(errorCodeVar.Get(ctx) ?? "issue-update-failed")) }, "Output errorCode"),
                // Surface the activity's rich human Error reason (not a duplicate of
                // errorCode) so callers/observability see WHY it failed; fall back to
                // the stable constant only when no reason was captured.
                WithLabel(new SetOutput { Id = "OutFailReason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)(NullIfBlank(errorVar.Get(ctx)) ?? "issue-update-failed")) }, "Output exitReason"),
            }
        };
        failureOutputs.SetDisplayText("Failure Outputs");

        var emitFailed = new EmitIssueStatusEventActivity
        {
            Id = "EmitFailed", Name = "Emit ISSUE_STATUS.UPDATED.FAILED",
            EventType = new Input<string>(_ => IssueStatusEvents.UpdatedFailed),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildFailureData(
                errorCodeVar.Get(ctx), errorVar.Get(ctx), issueNumberVar.Get(ctx),
                ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitFailed.SetDisplayText("Emit ISSUE_STATUS.UPDATED.FAILED");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "UpdateIssueStatusFlowchart",
            Name = "Update Issue Status Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, updateIssue,
                emitSuccess, successOutputs,
                failureOutputs, emitFailed, finish,
            },
            Connections =
            {
                Connect(readInputs, updateIssue),

                // Updated → success path
                ConnectOutcome(updateIssue, "Updated", emitSuccess),
                Connect(emitSuccess, successOutputs),
                Connect(successOutputs, finish),

                // Failed → explicit failure path (NO fall-through to success)
                ConnectOutcome(updateIssue, "Failed", failureOutputs),
                Connect(failureOutputs, emitFailed),
                Connect(emitFailed, finish),
            }
        };
    }

    private static string[]? NullIfEmpty(string[]? labels)
        => labels is { Length: > 0 } ? labels : null;

    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    private static long ElapsedMs(long startedAtTicks)
    {
        if (startedAtTicks <= 0) return 0;
        var elapsed = DateTime.UtcNow.Ticks - startedAtTicks;
        return elapsed > 0 ? elapsed / TimeSpan.TicksPerMillisecond : 0;
    }

    private static string BuildSuccessData(
        string message, string[]? addLabels, string[]? removeLabels,
        int prNumber, bool degraded, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["message"] = Truncate(message, 280),
            ["addLabels"] = addLabels ?? Array.Empty<string>(),
            ["removeLabels"] = removeLabels ?? Array.Empty<string>(),
            ["prNumber"] = prNumber,
            ["degraded"] = degraded,
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string BuildFailureData(
        string? errorCode, string? error, int issueNumber, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["issueNumber"] = issueNumber,
            ["errorCode"] = errorCode ?? "issue-update-failed",
            ["error"] = Truncate(error, 280),
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
