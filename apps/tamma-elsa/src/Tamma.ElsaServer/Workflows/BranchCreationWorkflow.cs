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
/// Branch Creation — create (or idempotently reuse) the feature branch that
/// isolates an issue's autonomous-development work, cut from a configurable base
/// branch, with conflict resolution and post-create validation.
///
/// <para>Story 2.4 build-out: the workflow is a real flowchart with an EXPLICIT
/// failure edge. The thin wrapper's dangling <c>Error</c> outcome (which left the
/// activity's failure with nowhere to go and reported a swallowed
/// <c>success=false</c>) is replaced: on a genuine create failure the activity
/// surfaces an <c>Error</c> outcome that emits <c>BRANCH.CREATED.FAILED</c>
/// (durable DCB drain) and sets <c>success=false</c>; success emits
/// <c>BRANCH.CREATED.SUCCESS</c> with the base SHA. No <c>Error</c> edge ever
/// falls through to the success outputs — no false success.</para>
///
/// Flow:
///   ReadInputs → CreateBranch
///     ├─ Created → EmitSuccess → Success Outputs → Finish
///     └─ Error   → Failure Outputs (success=false) → EmitFailed → Finish
///
/// Inputs:  repository, issueNumber, issueTitle? (else derived from workItemJson),
///          baseBranch?, conflictStrategy?, tenantId?, workItemJson?.
/// Outputs: success, branchName, baseSha (success path) / errorCode, error (failure path).
/// </summary>
public class BranchCreationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Branch Creation";
        builder.DefinitionId = "branch-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Create a feature branch (configurable base, idempotent conflict resolution) with an explicit failure path and durable DCB events";

        // ================================================================
        // Variables
        // ================================================================
        var repositoryVar = builder.WithVariable<string>("Repository", "").Persisted();
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0).Persisted();
        var issueTitleVar = builder.WithVariable<string>("IssueTitle", "").Persisted();
        var baseBranchVar = builder.WithVariable<string>("BaseBranch", "main").Persisted();
        var conflictStrategyVar = builder.WithVariable<string>("ConflictStrategy", "suffix").Persisted();
        var tenantIdVar = builder.WithVariable<string>("TenantId", "").Persisted();

        var branchNameVar = builder.WithVariable<string>("BranchName", "").Persisted();
        var baseShaVar = builder.WithVariable<string>("BaseSha", "").Persisted();
        var conflictResolvedVar = builder.WithVariable<bool>("ConflictResolved", false).Persisted();
        var errorCodeVar = builder.WithVariable<string>("ErrorCode", "").Persisted();
        var errorVar = builder.WithVariable<string>("Error", "").Persisted();
        var startedAtTicksVar = builder.WithVariable<long>("StartedAtTicks", 0).Persisted();

        // ================================================================
        // 1. Read inputs (derive issueTitle from workItemJson when not supplied —
        //    the real cycle path passes workItemJson, not issueTitle: gap #9).
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repositoryVar,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumberVar.Set(ctx, ctx.GetInput<int>("issueNumber"));

                var title = ctx.GetInput<string>("issueTitle");
                if (string.IsNullOrWhiteSpace(title))
                    title = ExtractWorkItemTitle(ctx.GetInput<string>("workItemJson"));
                issueTitleVar.Set(ctx, title ?? "");

                var baseBranch = ctx.GetInput<string>("baseBranch");
                baseBranchVar.Set(ctx, string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch!);

                var strategy = ctx.GetInput<string>("conflictStrategy");
                conflictStrategyVar.Set(ctx, string.IsNullOrWhiteSpace(strategy) ? "suffix" : strategy!);

                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                startedAtTicksVar.Set(ctx, DateTime.UtcNow.Ticks);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Create the branch (outcome-bearing — Created / Error)
        // ================================================================
        var createBranch = new CreateBranchActivity
        {
            Id = "CreateBranch", Name = "Create Branch",
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            IssueTitle = new Input<string>(ctx => issueTitleVar.Get(ctx)),
            BaseBranch = new Input<string>(ctx => baseBranchVar.Get(ctx)),
            ConflictStrategy = new Input<string>(ctx => conflictStrategyVar.Get(ctx)),
            BranchName = new Output<string?>(branchNameVar),
            BaseSha = new Output<string?>(baseShaVar),
            ConflictResolved = new Output<bool>(conflictResolvedVar),
            ErrorCode = new Output<string?>(errorCodeVar),
            Error = new Output<string?>(errorVar),
        };
        createBranch.SetDisplayText("Create Branch");

        // ================================================================
        // 3. Success path — emit BRANCH.CREATED.SUCCESS + outputs
        // ================================================================
        var emitSuccess = new EmitBranchEventActivity
        {
            Id = "EmitSuccess", Name = "Emit BRANCH.CREATED.SUCCESS",
            EventType = new Input<string>(_ => BranchEvents.CreatedSuccess),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildSuccessData(
                branchNameVar.Get(ctx), baseBranchVar.Get(ctx), baseShaVar.Get(ctx),
                conflictResolvedVar.Get(ctx), ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitSuccess.SetDisplayText("Emit BRANCH.CREATED.SUCCESS");

        var successOutputs = new Sequence
        {
            Id = "SuccessOutputs", Name = "Success Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutBranchName", OutputName = new("branchName"), OutputValue = new(ctx => (object)(branchNameVar.Get(ctx) ?? "")) }, "Output branchName"),
                WithLabel(new SetOutput { Id = "OutBaseSha", OutputName = new("baseSha"), OutputValue = new(ctx => (object)(baseShaVar.Get(ctx) ?? "")) }, "Output baseSha"),
                WithLabel(new SetOutput { Id = "OutBaseBranch", OutputName = new("baseBranch"), OutputValue = new(ctx => (object)baseBranchVar.Get(ctx)) }, "Output baseBranch"),
                WithLabel(new SetOutput { Id = "OutConflictResolved", OutputName = new("conflictResolved"), OutputValue = new(ctx => (object)conflictResolvedVar.Get(ctx)) }, "Output conflictResolved"),
            }
        };
        successOutputs.SetDisplayText("Success Outputs");

        // ================================================================
        // 4. Failure path — success=false, emit BRANCH.CREATED.FAILED
        //    (NO fall-through to success; branchName forced empty — no false branch)
        // ================================================================
        var failureOutputs = new Sequence
        {
            Id = "FailureOutputs", Name = "Failure Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutFailSuccess", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success=false"),
                WithLabel(new SetOutput { Id = "OutFailBranchName", OutputName = new("branchName"), OutputValue = new(_ => (object)"") }, "Output branchName="),
                WithLabel(new SetOutput { Id = "OutFailErrorCode", OutputName = new("errorCode"), OutputValue = new(ctx => (object)(NullIfBlank(errorCodeVar.Get(ctx)) ?? "branch-creation-failed")) }, "Output errorCode"),
                // Surface the activity's rich human Error reason (not a duplicate of
                // errorCode) so callers/observability see WHY it failed; fall back to
                // the stable constant only when no reason was captured.
                WithLabel(new SetOutput { Id = "OutFailReason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)(NullIfBlank(errorVar.Get(ctx)) ?? "branch-creation-failed")) }, "Output exitReason"),
            }
        };
        failureOutputs.SetDisplayText("Failure Outputs");

        var emitFailed = new EmitBranchEventActivity
        {
            Id = "EmitFailed", Name = "Emit BRANCH.CREATED.FAILED",
            EventType = new Input<string>(_ => BranchEvents.CreatedFailed),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildFailureData(
                errorCodeVar.Get(ctx), errorVar.Get(ctx), baseBranchVar.Get(ctx),
                ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitFailed.SetDisplayText("Emit BRANCH.CREATED.FAILED");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "BranchCreationFlowchart",
            Name = "Branch Creation Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, createBranch,
                emitSuccess, successOutputs,
                failureOutputs, emitFailed, finish,
            },
            Connections =
            {
                Connect(readInputs, createBranch),

                // Created → success path
                ConnectOutcome(createBranch, "Created", emitSuccess),
                Connect(emitSuccess, successOutputs),
                Connect(successOutputs, finish),

                // Error → explicit failure path (NO fall-through to success)
                ConnectOutcome(createBranch, "Error", failureOutputs),
                Connect(failureOutputs, emitFailed),
                Connect(emitFailed, finish),
            }
        };
    }

    /// <summary>
    /// Derive the issue title from the work-item JSON the cycle dispatches (it sends
    /// <c>workItemJson</c>, not <c>issueTitle</c> — gap #9). Mirrors
    /// <c>SingleIssueCycleWorkflow.ExtractWorkItemTitle</c>.
    /// </summary>
    private static string ExtractWorkItemTitle(string? workItemJson)
    {
        if (string.IsNullOrWhiteSpace(workItemJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(workItemJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var name in new[] { "title", "Title", "name", "Name" })
            {
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    private static long ElapsedMs(long startedAtTicks)
    {
        if (startedAtTicks <= 0) return 0;
        var elapsed = DateTime.UtcNow.Ticks - startedAtTicks;
        return elapsed > 0 ? elapsed / TimeSpan.TicksPerMillisecond : 0;
    }

    private static string BuildSuccessData(
        string? branchName, string baseBranch, string? baseSha, bool conflictResolved, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["finalName"] = branchName ?? "",
            ["baseBranch"] = baseBranch,
            ["baseSha"] = baseSha ?? "",
            ["conflictResolved"] = conflictResolved,
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string BuildFailureData(
        string? errorCode, string? error, string baseBranch, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["errorCode"] = NullIfBlank(errorCode) ?? "branch-creation-failed",
            ["error"] = Truncate(error, 280),
            ["baseBranch"] = baseBranch,
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
