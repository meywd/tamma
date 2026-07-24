using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// TDD with Debug Retry sub-workflow — a pivot-clean orchestrator that wraps the
/// <c>tdd-cycle</c> red-green-refactor workflow in a graph-enforced, bounded
/// debug-retry loop. On a TDD failure it dispatches the <c>debugging</c> workflow and
/// re-runs the cycle, up to <c>maxRetries</c> (default 3), then emits a
/// <c>success</c>/<c>errorMessage</c> contract. Holds no provider key — all LLM work
/// is inside the dispatched sub-workflows (mediated through <c>llm-call</c>).
///
/// <para>Build-out (completeness audit 2026-06-22, <c>TddWithDebugRetry.md</c>): the
/// thin extraction grew an explicit <b>exhaustion terminal</b> (a loud
/// <c>TDD_DEBUG.RETRY.EXHAUSTED</c> event + <c>success=false</c> with the REAL
/// underlying failure surfaced, never a silent success or a generic "limit reached"
/// string), run-level <c>TDD_DEBUG.*</c> DCB events via the durable engine drain, a
/// reset-on-entry attempt counter exposed as output (sibling parity with
/// <c>CiWithDebugRetryWorkflow</c>), a stable per-issue session id so retries correlate
/// into one TDD session, and — critically for an orchestrator — routing of EVERY
/// dispatched sub-workflow outcome to a terminal: the <c>debugging</c> result is now
/// inspected, so a debugger escalation short-circuits to a loud failure instead of
/// looping back and burning a retry on a known-unfixable failure. No dangling /
/// silent-failure edge; the bound is enforced by the <c>TddDebugGuard</c> FlowDecision.</para>
///
/// Flow:
///   init (reset attempt, derive sessionId)
///     → EmitCycleStarted → DispatchTddCycle → TddSuccess?
///       True  → EmitCyclePassed → EmitCompletedSuccess → FinishSuccess → Finish
///       False → EmitCycleFailed → TddDebugGuard?
///         False (exhausted) → EmitRetryExhausted → FinishFailure → Finish
///         True  (budget left) → IncrTddDebug → EmitDebugAttempted → DispatchTddDebugging
///                               → DebuggerEscalated?
///                                 True  (debugger escalated) → EmitDebuggerEscalated → FinishFailure → Finish
///                                 False (debugger fixed)     → EmitCycleStarted (loop)
///
/// Inputs:  storyId, planJson, repositoryUrl, branchName, skillLevel, issueNumber,
///          tenantId, maxRetries (optional)
/// Outputs: success (bool), errorMessage (string), finishReason (string),
///          tddDebugAttempt (int)
/// </summary>
public class TddWithDebugRetryWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "TDD with Debug Retry";
        builder.DefinitionId = "tdd-with-debug-retry";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Dispatches TDD cycle with a graph-enforced, bounded debug-retry loop and an explicit exhaustion terminal";

        // ================================================================
        // Variables
        // ================================================================
        var storyId = builder.WithVariable<string>("StoryId", "");
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var repositoryUrl = builder.WithVariable<string>("RepositoryUrl", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var skillLevel = builder.WithVariable<int>("SkillLevel", 5);
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var tenantId = builder.WithVariable<string>("TenantId", "");
        // tddDebugAttempt is ALWAYS reset to 0 on entry (see initInputs) so each
        // invocation — including a re-dispatch from a parent re-run — gets the full
        // retry budget regardless of any stale counter (sibling parity with
        // CiWithDebugRetryWorkflow's ciRetryCount reset).
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);
        var maxRetries = builder.WithVariable<int>("MaxRetries", 3);
        // Stable per-issue/story TDD session id, derived once in init and shared by the
        // tdd-cycle and debugging dispatches so retries correlate into ONE TDD session
        // (instead of a fresh Guid per dispatch). Lets resumed runs dedupe.
        var sessionId = builder.WithVariable<string>("SessionId", "");
        // Real underlying failure detail captured from the failing tdd-cycle result so
        // the failure terminal can surface the actual cause (not a generic string).
        var lastTddError = builder.WithVariable<string>("LastTddError", "TDD cycle failed");
        // The terminal failure reason: tdd-not-converged (exhausted) vs debugger-escalated.
        var finishReason = builder.WithVariable<string>("FinishReason", TddDebugEvents.ReasonNotConverged);

        // DispatchWorkflow result capture
        var tddResult = builder.WithVariable<IDictionary<string, object>?>();
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // INIT: Capture inputs, reset counter, derive a stable session id
        // ================================================================
        var initInputs = new SetVariable
        {
            Id = "InitTddRetryInputs",
            Name = "Init Inputs",
            Variable = storyId,
            Value = new Input<object?>(ctx =>
            {
                var plan = ctx.GetInput<string>("planJson");
                if (!string.IsNullOrEmpty(plan)) planJson.Set(ctx, plan);
                var repo = ctx.GetInput<string>("repositoryUrl");
                if (!string.IsNullOrEmpty(repo)) repositoryUrl.Set(ctx, repo);
                var branch = ctx.GetInput<string>("branchName");
                if (!string.IsNullOrEmpty(branch)) branchName.Set(ctx, branch);
                var skill = ctx.GetInput<int>("skillLevel");
                if (skill > 0) skillLevel.Set(ctx, skill);
                var issue = ctx.GetInput<int>("issueNumber");
                if (issue > 0) issueNumber.Set(ctx, issue);
                var acctId = ctx.GetInput<string>("tenantId");
                if (!string.IsNullOrEmpty(acctId)) tenantId.Set(ctx, acctId);
                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRetries.HasValue) maxRetries.Set(ctx, inputMaxRetries.Value);

                var resolvedStory = ctx.GetInput<string>("storyId") ?? "";

                // Reset the retry counter on every entry (full budget per invocation).
                tddDebugAttempt.Set(ctx, 0);

                // Derive a stable TDD session id shared across the cycle + debug
                // dispatches so retries correlate into one session. Falls back to a
                // fresh Guid only when neither issueNumber nor storyId is known.
                var derivedIssue = issue > 0 ? issue : ctx.GetInput<int>("issueNumber");
                var sid = derivedIssue > 0 || !string.IsNullOrEmpty(resolvedStory)
                    ? $"tdd-{(derivedIssue > 0 ? derivedIssue.ToString() : "0")}-{resolvedStory}"
                    : $"tdd-{Guid.NewGuid():N}";
                sessionId.Set(ctx, sid);

                return (object)resolvedStory;
            })
        };
        initInputs.SetDisplayText("Init Inputs");

        // ================================================================
        // DCB: TDD_DEBUG.CYCLE.STARTED — emitted before EACH cycle dispatch
        //      (also the loop re-entry point, so every dispatch is audited).
        // ================================================================
        var emitCycleStarted = new EmitTddDebugEventActivity
        {
            Id = "EmitCycleStarted", Name = "Emit TDD_DEBUG.CYCLE.STARTED",
            EventType = new Input<string>(_ => TddDebugEvents.CycleStarted),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
        };
        emitCycleStarted.SetDisplayText("Emit TDD_DEBUG.CYCLE.STARTED");

        // ================================================================
        // TDD Cycle (dispatch to existing tdd-cycle workflow)
        // ================================================================
        var tddCycle = new DispatchWorkflow
        {
            Id = "DispatchTddCycle",
            Name = "TDD Cycle",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = sessionId.Get(ctx),
                ["storyId"] = storyId.Get(ctx),
                ["taskDescription"] = planJson.Get(ctx),
                ["taskFiles"] = new List<string>(),
                ["repositoryUrl"] = repositoryUrl.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        tddCycle.SetDisplayText("TDD Cycle");

        // ================================================================
        // TDD Success check (gate on the dispatched cycle's success — no false success)
        // ================================================================
        var tddSuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddSuccess", Name = "TDD Passed?" };
        tddSuccess.SetDisplayText("TDD Passed?");

        // ================================================================
        // DCB: TDD_DEBUG.CYCLE.PASSED (success path)
        // ================================================================
        var emitCyclePassed = new EmitTddDebugEventActivity
        {
            Id = "EmitCyclePassed", Name = "Emit TDD_DEBUG.CYCLE.PASSED",
            EventType = new Input<string>(_ => TddDebugEvents.CyclePassed),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
        };
        emitCyclePassed.SetDisplayText("Emit TDD_DEBUG.CYCLE.PASSED");

        var emitCompletedSuccess = new EmitTddDebugEventActivity
        {
            Id = "EmitCompletedSuccess", Name = "Emit TDD_DEBUG.COMPLETED.SUCCESS",
            EventType = new Input<string>(_ => TddDebugEvents.CompletedSuccess),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
        };
        emitCompletedSuccess.SetDisplayText("Emit TDD_DEBUG.COMPLETED.SUCCESS");

        // ================================================================
        // DCB: TDD_DEBUG.CYCLE.FAILED (failure path) — also captures the REAL error
        //      detail so the eventual failure terminal can surface the cause.
        // ================================================================
        var captureTddError = new SetVariable
        {
            Id = "CaptureTddError", Name = "Capture TDD Error",
            Variable = lastTddError,
            Value = new Input<object?>(ctx => (object)GetTddErrorOutput(tddResult.Get(ctx)))
        };
        captureTddError.SetDisplayText("Capture TDD Error");

        var emitCycleFailed = new EmitTddDebugEventActivity
        {
            Id = "EmitCycleFailed", Name = "Emit TDD_DEBUG.CYCLE.FAILED",
            EventType = new Input<string>(_ => TddDebugEvents.CycleFailed),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
            ErrorDetail = new Input<string?>(ctx => lastTddError.Get(ctx)),
        };
        emitCycleFailed.SetDisplayText("Emit TDD_DEBUG.CYCLE.FAILED");

        // ================================================================
        // TDD Debug retry guard (graph-enforced bound: < maxRetries)
        // ================================================================
        var tddDebugGuard = new FlowDecision(ctx => tddDebugAttempt.Get(ctx) < maxRetries.Get(ctx))
        { Id = "TddDebugGuard", Name = "TDD Debug < Max?" };
        tddDebugGuard.SetDisplayText("TDD Debug < Max?");

        // ================================================================
        // Increment TDD debug counter
        // ================================================================
        var incrementTddDebug = new SetVariable
        {
            Id = "IncrTddDebug",
            Name = "Increment TDD Debug",
            Variable = tddDebugAttempt,
            Value = new Input<object?>(ctx => (object)(tddDebugAttempt.Get(ctx) + 1))
        };
        incrementTddDebug.SetDisplayText("Increment TDD Debug");

        // ================================================================
        // DCB: TDD_DEBUG.DEBUG.ATTEMPTED
        // ================================================================
        var emitDebugAttempted = new EmitTddDebugEventActivity
        {
            Id = "EmitDebugAttempted", Name = "Emit TDD_DEBUG.DEBUG.ATTEMPTED",
            EventType = new Input<string>(_ => TddDebugEvents.DebugAttempted),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
            ErrorDetail = new Input<string?>(ctx => lastTddError.Get(ctx)),
        };
        emitDebugAttempted.SetDisplayText("Emit TDD_DEBUG.DEBUG.ATTEMPTED");

        // ================================================================
        // Dispatch debugging for TDD failure (shares the stable session id)
        // ================================================================
        var dispatchTddDebugging = new DispatchWorkflow
        {
            Id = "DispatchTddDebugging",
            Name = "Debug TDD Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = sessionId.Get(ctx),
                ["storyId"] = storyId.Get(ctx),
                ["debugContextMode"] = "TddFailure",
                ["errorOutput"] = lastTddError.Get(ctx),
                ["repositoryUrl"] = repositoryUrl.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
                // Story 39-15 (D4) — capture the prior attempt's typed diagnosis id (additive
                // debugging output) so attempt N's Diagnosis supersedes N-1's (the time-travel
                // lineage). Empty on the first attempt; carried from the previous debugResult after.
                ["priorDiagnosisDocumentId"] = ReadDiagnosisDocumentId(debugResult.Get(ctx)),
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchTddDebugging.SetDisplayText("Debug TDD Failure");

        // ================================================================
        // Debugger escalation gate — INSPECT the dispatched debugging result.
        // The debugging workflow returns success=false when it ESCALATED (could not
        // fix). True here => escalated => short-circuit to a loud failure instead of
        // looping back and burning a retry on a known-unfixable failure (cap. 9).
        // ================================================================
        var debuggerEscalated = new FlowDecision(ctx =>
        {
            var result = debugResult.Get(ctx);
            // Escalated when the debugger explicitly reported success=false.
            if (result != null && result.TryGetValue("success", out var s))
                return !(s is true || s?.ToString() == "True");
            // No usable result => treat as escalated (don't loop on an unknown).
            return result == null;
        })
        { Id = "DebuggerEscalated", Name = "Debugger Escalated?" };
        debuggerEscalated.SetDisplayText("Debugger Escalated?");

        var setReasonEscalated = new SetVariable
        {
            Id = "SetReasonEscalated", Name = "Set Reason Escalated",
            Variable = finishReason,
            Value = new Input<object?>(_ => (object)TddDebugEvents.ReasonDebuggerEscalated)
        };
        setReasonEscalated.SetDisplayText("Set Reason Escalated");

        var emitDebuggerEscalated = new EmitTddDebugEventActivity
        {
            Id = "EmitDebuggerEscalated", Name = "Emit TDD_DEBUG.DEBUGGER.ESCALATED",
            EventType = new Input<string>(_ => TddDebugEvents.DebuggerEscalated),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
            FinishReason = new Input<string?>(_ => TddDebugEvents.ReasonDebuggerEscalated),
            ErrorDetail = new Input<string?>(ctx => lastTddError.Get(ctx)),
        };
        emitDebuggerEscalated.SetDisplayText("Emit TDD_DEBUG.DEBUGGER.ESCALATED");

        // ================================================================
        // Retry exhaustion terminal — LOUD, never a silent success.
        // ================================================================
        var setReasonNotConverged = new SetVariable
        {
            Id = "SetReasonNotConverged", Name = "Set Reason Not Converged",
            Variable = finishReason,
            Value = new Input<object?>(_ => (object)TddDebugEvents.ReasonNotConverged)
        };
        setReasonNotConverged.SetDisplayText("Set Reason Not Converged");

        var emitRetryExhausted = new EmitTddDebugEventActivity
        {
            Id = "EmitRetryExhausted", Name = "Emit TDD_DEBUG.RETRY.EXHAUSTED",
            EventType = new Input<string>(_ => TddDebugEvents.RetryExhausted),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repositoryUrl.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => tddDebugAttempt.Get(ctx)),
            MaxRetries = new Input<int>(ctx => maxRetries.Get(ctx)),
            FinishReason = new Input<string?>(_ => TddDebugEvents.ReasonNotConverged),
            ErrorDetail = new Input<string?>(ctx => lastTddError.Get(ctx)),
        };
        emitRetryExhausted.SetDisplayText("Emit TDD_DEBUG.RETRY.EXHAUSTED");

        // ================================================================
        // Finish: Success outputs (exposes the attempts counter — sibling parity)
        // ================================================================
        var finishSuccessOutputs = new Sequence
        {
            Id = "TddRetryFinishSuccess",
            Name = "Finish Success",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetTddRetrySuccess", Name = "Set Success", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Set Success"),
                WithLabel(new SetOutput { Id = "SetTddRetryErrorEmpty", Name = "Set Error Empty", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"") }, "Set Error Empty"),
                WithLabel(new SetOutput { Id = "SetTddRetryReasonEmpty", Name = "Set Reason Empty", OutputName = new("finishReason"), OutputValue = new(_ => (object)"") }, "Set Reason Empty"),
                WithLabel(new SetOutput { Id = "SetTddRetryAttemptsPass", Name = "Set Attempts", OutputName = new("tddDebugAttempt"), OutputValue = new(ctx => (object)tddDebugAttempt.Get(ctx)) }, "Set Attempts")
            }
        };
        finishSuccessOutputs.SetDisplayText("Finish Success");

        // ================================================================
        // Finish: Failure outputs — surfaces the REAL failure detail + finishReason +
        // attempts. No generic "limit reached" string; no false success.
        // ================================================================
        var finishFailureOutputs = new Sequence
        {
            Id = "TddRetryFinishFailure",
            Name = "Finish Failure",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetTddRetryFailed", Name = "Set Failed", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Set Failed"),
                WithLabel(new SetOutput { Id = "SetTddRetryErrorMsg", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(ctx => (object)BuildFailureMessage(finishReason.Get(ctx), tddDebugAttempt.Get(ctx), maxRetries.Get(ctx), lastTddError.Get(ctx))) }, "Set Error Message"),
                WithLabel(new SetOutput { Id = "SetTddRetryReason", Name = "Set Finish Reason", OutputName = new("finishReason"), OutputValue = new(ctx => (object)finishReason.Get(ctx)) }, "Set Finish Reason"),
                WithLabel(new SetOutput { Id = "SetTddRetryAttemptsFail", Name = "Set Attempts", OutputName = new("tddDebugAttempt"), OutputValue = new(ctx => (object)tddDebugAttempt.Get(ctx)) }, "Set Attempts")
            }
        };
        finishFailureOutputs.SetDisplayText("Finish Failure");

        var finish = new Finish { Id = "TddRetryFinish", Name = "Complete: TDD Retry Done" };
        finish.SetDisplayText("Complete: TDD Retry Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TddWithDebugRetryFlowchart",
            Name = "TDD with Debug Retry Flowchart",
            Start = initInputs,
            Activities =
            {
                initInputs,
                emitCycleStarted, tddCycle, tddSuccess,
                emitCyclePassed, emitCompletedSuccess,
                captureTddError, emitCycleFailed, tddDebugGuard,
                setReasonNotConverged, emitRetryExhausted,
                incrementTddDebug, emitDebugAttempted, dispatchTddDebugging,
                debuggerEscalated, setReasonEscalated, emitDebuggerEscalated,
                finishSuccessOutputs, finishFailureOutputs, finish
            },
            Connections =
            {
                // Init -> STARTED -> TDD Cycle
                Connect(initInputs, emitCycleStarted),
                Connect(emitCycleStarted, tddCycle),

                // TDD Cycle -> Success gate
                Connect(tddCycle, tddSuccess),

                // TDD passed -> PASSED -> COMPLETED.SUCCESS -> success terminal
                ConnectOutcome(tddSuccess, "True", emitCyclePassed),
                Connect(emitCyclePassed, emitCompletedSuccess),
                Connect(emitCompletedSuccess, finishSuccessOutputs),
                Connect(finishSuccessOutputs, finish),

                // TDD failed -> capture cause -> FAILED -> retry guard (the bound)
                ConnectOutcome(tddSuccess, "False", captureTddError),
                Connect(captureTddError, emitCycleFailed),
                Connect(emitCycleFailed, tddDebugGuard),

                // Guard False (exhausted) -> reason -> EXHAUSTED -> failure terminal (LOUD)
                ConnectOutcome(tddDebugGuard, "False", setReasonNotConverged),
                Connect(setReasonNotConverged, emitRetryExhausted),
                Connect(emitRetryExhausted, finishFailureOutputs),

                // Guard True (budget left) -> increment -> ATTEMPTED -> dispatch debugging
                ConnectOutcome(tddDebugGuard, "True", incrementTddDebug),
                Connect(incrementTddDebug, emitDebugAttempted),
                Connect(emitDebugAttempted, dispatchTddDebugging),

                // Debugging result -> escalation gate (every sub-workflow outcome routed)
                Connect(dispatchTddDebugging, debuggerEscalated),

                // Debugger escalated -> reason -> ESCALATED -> failure terminal (LOUD)
                ConnectOutcome(debuggerEscalated, "True", setReasonEscalated),
                Connect(setReasonEscalated, emitDebuggerEscalated),
                Connect(emitDebuggerEscalated, finishFailureOutputs),

                // Debugger fixed (soft) -> loop back to re-test (via STARTED, audited)
                ConnectOutcome(debuggerEscalated, "False", emitCycleStarted),

                // Failure terminal -> finish
                Connect(finishFailureOutputs, finish)
            }
        };
    }

    // ================================================================
    // Helper methods
    // ================================================================

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

    /// <summary>
    /// Extract the real underlying TDD failure detail from the dispatched
    /// <c>tdd-cycle</c> result (its <c>errorMessage</c> / <c>finishReason</c> output),
    /// so the orchestrator can surface the cause instead of a generic string. Never
    /// returns empty (no silent failure).
    /// </summary>
    private static string GetTddErrorOutput(IDictionary<string, object>? result)
    {
        if (result == null) return "TDD cycle failed with no result (sub-workflow did not complete)";
        if (result.TryGetValue("errorMessage", out var err) && !string.IsNullOrWhiteSpace(err?.ToString()))
            return err!.ToString()!;
        if (result.TryGetValue("finishReason", out var reason) && !string.IsNullOrWhiteSpace(reason?.ToString()))
            return $"TDD cycle failed ({reason})";
        return "TDD cycle failed with unknown error";
    }

    /// <summary>
    /// Build the failure <c>errorMessage</c> from the real cause + the finish reason +
    /// the retry budget — distinguishing a genuine non-convergence (exhausted) from a
    /// debugger escalation, and ALWAYS surfacing the underlying detail. Exposed for
    /// unit testing.
    /// </summary>
    public static string BuildFailureMessage(string finishReason, int attempts, int maxRetries, string lastError)
    {
        var detail = string.IsNullOrWhiteSpace(lastError) ? "TDD cycle failed" : lastError;
        return finishReason == TddDebugEvents.ReasonDebuggerEscalated
            ? $"TDD debugging escalated after {attempts}/{maxRetries} attempt(s): {detail}"
            : $"TDD did not converge after {attempts}/{maxRetries} debug attempt(s): {detail}";
    }

    /// <summary>
    /// Story 39-15 (D4) — read the additive <c>diagnosisDocumentId</c> from a prior debugging
    /// dispatch result (the typed diagnosis id). Empty on the first attempt / a null result, so
    /// the next attempt supersedes the prior diagnosis. Pure; exposed for unit testing.
    /// </summary>
    public static string ReadDiagnosisDocumentId(IDictionary<string, object>? debugResult)
        => debugResult != null && debugResult.TryGetValue("diagnosisDocumentId", out var d)
            ? d?.ToString() ?? ""
            : "";
}
