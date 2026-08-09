using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Pull Request — open (or idempotently reuse) a PR from the feature branch to
/// the base branch, with an AI-generated (call-LLM mediated) description plus a
/// deterministic fallback, change + test summaries, smart labels and reviewers.
///
/// <para>Story 2.8 build-out: every failure path is explicit and emits
/// <c>PR.CREATED.FAILED</c>; success emits <c>PR.CREATED.SUCCESS</c> and bumps
/// <c>prs_created_total</c>. The caller's <c>draft</c> flag is honoured
/// end-to-end. No silent false success — the <c>Error</c> outcome never falls
/// through to the success outputs.</para>
///
/// Flow:
///   ReadInputs → GenerateDescription (llm-call) → Capture Description
///     → CreatePR
///         ├─ Created/Updated → EmitSuccess → Success Outputs → Finish
///         └─ Error           → Failure Outputs (success=false) → EmitFailed
///                              → SetExitReason → Finish
/// </summary>
public class PullRequestWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Pull Request";
        builder.DefinitionId = "pull-request";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Create a pull request with AI description, change/test summaries, labels and reviewers";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var baseBranchVar = builder.WithVariable<string>("BaseBranch", "main");
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0);
        var issueTitleVar = builder.WithVariable<string>("IssueTitle", "");
        var planJsonVar = builder.WithVariable<string>("PlanJson", "");
        var draftVar = builder.WithVariable<bool>("Draft", false);
        var tenantIdVar = builder.WithVariable<string>("TenantId", "");
        var changeSummaryVar = builder.WithVariable<string>("ChangeSummaryJson", "");
        var testSummaryVar = builder.WithVariable<string>("TestSummaryJson", "");
        var issueLabelsVar = builder.WithVariable<string>("IssueLabelsJson", "");
        var reviewersVar = builder.WithVariable<string>("ReviewersJson", "");

        var aiBodyVar = builder.WithVariable<string>("AiBody", "");
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        var prNumberVar = builder.WithVariable<int>("PrNumber", 0);
        var prUrlVar = builder.WithVariable<string>("PrUrl", "");
        var reusedVar = builder.WithVariable<bool>("Reused", false);
        var isDraftVar = builder.WithVariable<bool>("IsDraft", false);
        var appliedLabelsVar = builder.WithVariable<string>("AppliedLabels", "[]");
        var errorCodeVar = builder.WithVariable<string>("ErrorCode", "");
        var startedAtTicksVar = builder.WithVariable<long>("StartedAtTicks", 0);

        // ================================================================
        // 1. Read inputs
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                branchName.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                baseBranchVar.Set(ctx, ctx.GetInput<string>("baseBranch") ?? "main");
                issueNumberVar.Set(ctx, ctx.GetInput<int>("issueNumber"));
                issueTitleVar.Set(ctx, ctx.GetInput<string>("issueTitle") ?? "");
                planJsonVar.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                draftVar.Set(ctx, ctx.GetInput<bool?>("draft") ?? false);
                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                changeSummaryVar.Set(ctx, ctx.GetInput<string>("changeSummaryJson") ?? "");
                testSummaryVar.Set(ctx, ctx.GetInput<string>("testSummaryJson") ?? "");
                issueLabelsVar.Set(ctx, ctx.GetInput<string>("issueLabelsJson") ?? "");
                reviewersVar.Set(ctx, ctx.GetInput<string>("reviewersJson") ?? "");
                startedAtTicksVar.Set(ctx, DateTime.UtcNow.Ticks);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Generate description (mediated LLM — tech_writer / summarize-changes)
        //    LLM failure does NOT abort: the activity falls back deterministically.
        // ================================================================
        var generateDescription = new DispatchWorkflow
        {
            Id = "GenerateDescription", Name = "Generate Description",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.TechWriter.ToWire(),
                ["action"] = AgentAction.SummarizeChanges.ToWire(),
                ["tenantId"] = tenantIdVar.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["issueNumber"] = issueNumberVar.Get(ctx),
                    ["issueTitle"] = issueTitleVar.Get(ctx),
                    ["planJson"] = planJsonVar.Get(ctx),
                    ["changeSummary"] = changeSummaryVar.Get(ctx),
                    ["testSummary"] = testSummaryVar.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                    ["branchName"] = branchName.Get(ctx),
                    ["baseBranch"] = baseBranchVar.Get(ctx),
                },
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        generateDescription.SetDisplayText("Generate Description");

        // 2a. Capture the AI body (empty when the LLM failed → activity falls back).
        var captureDescription = new SetVariable
        {
            Id = "CaptureDescription", Name = "Capture Description",
            Variable = aiBodyVar,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result == null) return (object)"";
                var ok = result.TryGetValue("success", out var s) && s is true;
                if (ok && result.TryGetValue("llmResponse", out var r))
                    return (object)(r?.ToString() ?? "");
                return (object)"";
            })
        };
        captureDescription.SetDisplayText("Capture Description");

        // ================================================================
        // 2b. Epic 31 P5 M2 (DG-3) — the §4 IS-SUPPORTED CHECK STEP for the
        // reviewer request. Reviewers ride the PrLifecycle verb family
        // (RequestReviewersAsync); when the resolved driver positively lacks
        // it, the alternative step emits GIT.PR_REVIEWERS.SKIPPED and CLEARS
        // the reviewers input so the PR step runs without a doomed request.
        // No reviewers requested → the check is skipped entirely (nothing
        // capability-gated is being asked for). The PR step itself carries
        // the §4.3 safety net: a reviewer request the platform refuses at
        // runtime is captured by the mediation core (reviewersSkipped +
        // needs-reviewer label + the same audit event) and NEVER fails the
        // PR step.
        // ================================================================
        var hasReviewers = new FlowDecision(ctx =>
            ParseReviewerCount(reviewersVar.Get(ctx)) > 0)
        { Id = "HasReviewers", Name = "Reviewers Requested?" };
        hasReviewers.SetDisplayText("Reviewers Requested?");

        var checkReviewersSupported = new CheckPlatformCapabilityActivity
        {
            Id = "CheckReviewersSupported",
            Name = "Reviewer Request Supported?",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            Capability = new Input<string>("PrLifecycle"),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
        };
        checkReviewersSupported.SetDisplayText("Reviewer Request Supported?");

        // DG-3's alternative step — audited skip (silent skips are forbidden,
        // §4.4), then the reviewers input is cleared and the PR step proceeds.
        var markReviewersSkipped = new EmitPrEventActivity
        {
            Id = "MarkReviewersSkipped",
            Name = "Emit GIT.PR_REVIEWERS.SKIPPED",
            EventType = new Input<string>(_ => PrEvents.ReviewersSkipped),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            PrNumber = new Input<int>(_ => 0), // no PR yet — the skip precedes creation
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["reason"] = "capability_unsupported",
                ["reviewerCount"] = ParseReviewerCount(reviewersVar.Get(ctx)),
                ["decidedBy"] = "check-step",
            })),
        };
        markReviewersSkipped.SetDisplayText("Emit GIT.PR_REVIEWERS.SKIPPED");

        var clearReviewers = new SetVariable
        {
            Id = "ClearReviewers", Name = "Clear Reviewers (unsupported)",
            Variable = reviewersVar,
            Value = new Input<object?>(_ => (object)"[]"),
        };
        clearReviewers.SetDisplayText("Clear Reviewers (unsupported)");

        // ================================================================
        // 3. Create (or reuse/update) the PR
        // ================================================================
        var createPr = new CreatePullRequestActivity
        {
            Id = "CreatePR", Name = "Create PR",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            BranchName = new Input<string>(ctx => branchName.Get(ctx)),
            BaseBranch = new Input<string>(ctx => baseBranchVar.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            IssueTitle = new Input<string>(ctx => issueTitleVar.Get(ctx)),
            PlanJson = new Input<string?>(ctx => planJsonVar.Get(ctx)),
            Draft = new Input<bool>(ctx => draftVar.Get(ctx)),
            AiBody = new Input<string?>(ctx => aiBodyVar.Get(ctx)),
            ChangeSummaryJson = new Input<string?>(ctx => changeSummaryVar.Get(ctx)),
            TestSummaryJson = new Input<string?>(ctx => testSummaryVar.Get(ctx)),
            IssueLabelsJson = new Input<string?>(ctx => issueLabelsVar.Get(ctx)),
            ReviewersJson = new Input<string?>(ctx => reviewersVar.Get(ctx)),
            PrNumber = new Output<int>(prNumberVar),
            PrUrl = new Output<string?>(prUrlVar),
            Reused = new Output<bool>(reusedVar),
            IsDraft = new Output<bool>(isDraftVar),
            AppliedLabels = new Output<string?>(appliedLabelsVar),
            ErrorCode = new Output<string?>(errorCodeVar),
        };
        createPr.SetDisplayText("Create PR");

        // ================================================================
        // 4. Success path — emit PR.CREATED.SUCCESS + outputs
        // ================================================================
        var emitSuccess = new EmitPrEventActivity
        {
            Id = "EmitSuccess", Name = "Emit PR.CREATED.SUCCESS",
            EventType = new Input<string>(_ => PrEvents.CreatedSuccess),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildSuccessData(
                prUrlVar.Get(ctx), baseBranchVar.Get(ctx), branchName.Get(ctx),
                isDraftVar.Get(ctx), reusedVar.Get(ctx), appliedLabelsVar.Get(ctx),
                changeSummaryVar.Get(ctx), testSummaryVar.Get(ctx), reviewersVar.Get(ctx),
                ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitSuccess.SetDisplayText("Emit PR.CREATED.SUCCESS");

        var successOutputs = new Sequence
        {
            Id = "SuccessOutputs", Name = "Success Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutPrNumber", OutputName = new("prNumber"), OutputValue = new(ctx => (object)prNumberVar.Get(ctx)) }, "Output prNumber"),
                WithLabel(new SetOutput { Id = "OutPrUrl", OutputName = new("prUrl"), OutputValue = new(ctx => (object)(prUrlVar.Get(ctx) ?? "")) }, "Output prUrl"),
                WithLabel(new SetOutput { Id = "OutIsDraft", OutputName = new("isDraft"), OutputValue = new(ctx => (object)isDraftVar.Get(ctx)) }, "Output isDraft"),
                WithLabel(new SetOutput { Id = "OutBaseBranch", OutputName = new("baseBranch"), OutputValue = new(ctx => (object)baseBranchVar.Get(ctx)) }, "Output baseBranch"),
                WithLabel(new SetOutput { Id = "OutHeadBranch", OutputName = new("headBranch"), OutputValue = new(ctx => (object)branchName.Get(ctx)) }, "Output headBranch"),
                WithLabel(new SetOutput { Id = "OutLinkedIssue", OutputName = new("linkedIssue"), OutputValue = new(ctx => (object)issueNumberVar.Get(ctx)) }, "Output linkedIssue"),
                WithLabel(new SetOutput { Id = "OutReused", OutputName = new("reused"), OutputValue = new(ctx => (object)reusedVar.Get(ctx)) }, "Output reused"),
            }
        };
        successOutputs.SetDisplayText("Success Outputs");

        // ================================================================
        // 5. Failure path — success=false, emit PR.CREATED.FAILED, set exit reason
        // ================================================================
        var failureOutputs = new Sequence
        {
            Id = "FailureOutputs", Name = "Failure Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutFailSuccess", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success=false"),
                WithLabel(new SetOutput { Id = "OutFailPrNumber", OutputName = new("prNumber"), OutputValue = new(_ => (object)0) }, "Output prNumber=0"),
                WithLabel(new SetOutput { Id = "OutFailPrUrl", OutputName = new("prUrl"), OutputValue = new(_ => (object)"") }, "Output prUrl="),
                WithLabel(new SetOutput { Id = "OutFailErrorCode", OutputName = new("errorCode"), OutputValue = new(ctx => (object)(errorCodeVar.Get(ctx) ?? "pr-creation-failed")) }, "Output errorCode"),
                WithLabel(new SetOutput { Id = "OutFailReason", OutputName = new("exitReason"), OutputValue = new(_ => (object)"pr-creation-failed") }, "Output exitReason"),
            }
        };
        failureOutputs.SetDisplayText("Failure Outputs");

        var emitFailed = new EmitPrEventActivity
        {
            Id = "EmitFailed", Name = "Emit PR.CREATED.FAILED",
            EventType = new Input<string>(_ => PrEvents.CreatedFailed),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            PrNumber = new Input<int>(_ => 0),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildFailureData(
                errorCodeVar.Get(ctx), baseBranchVar.Get(ctx), branchName.Get(ctx),
                draftVar.Get(ctx), ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitFailed.SetDisplayText("Emit PR.CREATED.FAILED");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "PullRequestFlowchart",
            Name = "Pull Request Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, generateDescription, captureDescription,
                hasReviewers, checkReviewersSupported, markReviewersSkipped, clearReviewers,
                createPr,
                emitSuccess, successOutputs,
                failureOutputs, emitFailed, finish,
            },
            Connections =
            {
                Connect(readInputs, generateDescription),
                Connect(generateDescription, captureDescription),

                // Epic 31 P5 M2 (DG-3) — reviewers requested? → §4 check step
                // before the PR step (which performs the reviewer request);
                // unsupported → audited skip → clear reviewers → PR step.
                // No reviewers → straight to the PR step (nothing gated).
                Connect(captureDescription, hasReviewers),
                ConnectOutcome(hasReviewers, "False", createPr),
                ConnectOutcome(hasReviewers, "True", checkReviewersSupported),
                ConnectOutcome(checkReviewersSupported, "Supported", createPr),
                ConnectOutcome(checkReviewersSupported, "Unsupported", markReviewersSkipped),
                Connect(markReviewersSkipped, clearReviewers),
                Connect(clearReviewers, createPr),

                // Created / Updated → success path
                ConnectOutcome(createPr, "Created", emitSuccess),
                ConnectOutcome(createPr, "Updated", emitSuccess),
                Connect(emitSuccess, successOutputs),
                Connect(successOutputs, finish),

                // Error → explicit failure path (NO fall-through to success)
                ConnectOutcome(createPr, "Error", failureOutputs),
                Connect(failureOutputs, emitFailed),
                Connect(emitFailed, finish),
            }
        };
    }

    private static long ElapsedMs(long startedAtTicks)
    {
        if (startedAtTicks <= 0) return 0;
        var elapsed = DateTime.UtcNow.Ticks - startedAtTicks;
        return elapsed > 0 ? elapsed / TimeSpan.TicksPerMillisecond : 0;
    }

    private static string BuildSuccessData(
        string? url, string baseBranch, string headBranch, bool isDraft, bool reused,
        string? appliedLabelsJson, string? changeSummaryJson, string? testSummaryJson,
        string? reviewersJson, long durationMs)
    {
        var change = ChangeSummary.Parse(changeSummaryJson);
        var test = TestSummary.Parse(testSummaryJson);
        var labels = SafeList(appliedLabelsJson);
        var reviewers = SafeList(reviewersJson);

        var data = new Dictionary<string, object?>
        {
            ["url"] = url ?? "",
            ["base"] = baseBranch,
            ["head"] = headBranch,
            ["isDraft"] = isDraft,
            ["reused"] = reused,
            ["filesChanged"] = change?.FilesChanged ?? 0,
            ["linesAdded"] = change?.LinesAdded ?? 0,
            ["linesDeleted"] = change?.LinesDeleted ?? 0,
            ["testCoverage"] = test?.Coverage ?? 0d,
            ["reviewers"] = reviewers.Count,
            ["labels"] = labels.Count,
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string BuildFailureData(
        string? errorCode, string baseBranch, string headBranch, bool isDraft, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["error"] = errorCode ?? "pr-creation-failed",
            ["base"] = baseBranch,
            ["head"] = headBranch,
            ["isDraft"] = isDraft,
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static List<string> SafeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>DG-3 gate predicate: how many reviewers the caller actually
    /// requested (0 for empty/blank/unparseable — the check step only runs
    /// when a capability-gated request is really being made).</summary>
    internal static int ParseReviewerCount(string? reviewersJson) => SafeList(reviewersJson).Count;

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
