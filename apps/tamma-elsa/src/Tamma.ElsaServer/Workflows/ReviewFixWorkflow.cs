using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.CodeIndex;
using Tamma.Activities.Security;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Review Fix — the autonomous loop's review-closing phase (Story 2-18 Phases 4 &amp;
/// 5 / Story 2-9): fetch a PR's review comments, decide which are actionable,
/// generate code fixes via the mediated <c>llm-call</c> path, apply them, and index
/// the changed files so CI/review can re-run.
///
/// <para>Build-out: the prior version was a happy-path skeleton (analyze → decide →
/// generate → "apply" → index → constant <c>success=true</c>) with no failure
/// edges, an activity-internal-only loop bound, and zero audit events. This build-out
/// makes the load-bearing guarantees graph-enforced:</para>
///
/// <list type="bullet">
///   <item><description><b>Forward-installed iteration-bound scaffolding</b> — an
///     <c>Iteration</c> counter, a <c>MaxIterations</c> cap <see cref="FlowDecision"/>
///     (<c>&gt;=MaxIterations</c> → <c>REVIEW_FIX.ESCALATED</c> failure terminal), and
///     the escalate edge are wired AHEAD of the verify→regenerate retry loop they are
///     meant to bound. There is NO back-edge today — the connections are strictly
///     forward, so each invocation runs the analyze→generate→apply path at most once
///     and the counter only ever reaches 1; the <c>&gt;=MaxIterations</c> cap therefore
///     does NOT fire at runtime yet (it is structure, not yet a live loop guard). The
///     escalate path becomes load-bearing only once the verify loop-back lands (the
///     real file-write / verify / CI-retrigger step, deferred to Epic 38) and feeds a
///     failed verification back to <c>IncrementIteration</c>. The cap mirrors the
///     <c>PlanMaxRevisions</c>/<c>TaskMaxRevisions</c> / merge-approval test-loop bound
///     so the eventual loop bounds out the same way.</description></item>
///   <item><description><b>Explicit error / exhaustion path</b> —
///     <c>AnalyzeReview</c>'s <c>Error</c> outcome and a failed <c>llm-call</c>
///     (<c>success=false</c>, read from the dispatch result) each route to a loud
///     <c>OutputFailure</c> terminal. A failed generation NEVER flows into "apply"
///     as a false success.</description></item>
///   <item><description><b>Fail-closed apply</b> — <c>ApplyFixes.Error</c> AND a
///     <c>Fixed</c> outcome that did not actually apply files
///     (<c>fixesApplied=false</c>) route to the failure terminal. <c>success</c>
///     reflects reality — it is never a constant <c>true</c>.</description></item>
///   <item><description><b>DCB audit events</b> — <c>REVIEW_FIX.*</c> events on every
///     meaningful edge (analyze / generate / apply / escalate), via
///     <see cref="EmitReviewFixEventActivity"/> through the durable engine event
///     drain. The workflow carries <c>tenantId</c> so events are tenant-scoped.</description></item>
/// </list>
///
/// <para>Deferred (reported for confirmation):</para>
/// <list type="bullet">
///   <item><description>removing the direct-LLM fallback in
///     <c>ApplyReviewFixesActivity</c> (the <c>CallLlm</c> → Anthropic
///     <c>/v1/messages</c> path) and forcing the mediated response — that is
///     <b>32-5 T6</b>'s job; the LLM here already routes via <c>llm-call</c>;</description></item>
///   <item><description>mediating <c>AnalyzeReview</c>'s GitHub read and any git
///     writes (real apply / commit / push) through the internal API — that is
///     <b>Epic 38</b> (non-LLM / git mediation);</description></item>
///   <item><description>AI comment classification, verify-and-loop on lint/type
///     errors, commit &amp; push, re-request review, CI re-trigger, and wiring into
///     <c>SingleIssueCycleWorkflow</c> — gated on Epic 38 / 32-5 / Story 2-18
///     Phases 6-7 (spec §5 items #2,#8-#13).</description></item>
/// </list>
///
/// Flow:
///   DefaultOutcome (seed success=false) → ReadInputs → AnalyzeReview
///     ├─ Done  → EmitAnalyzeSuccess → HasActionable?
///     │            ├─ False → OutputSuccess (genuine: nothing to fix)
///     │            └─ True  → IncrementIteration → MaxIterations?
///     │                         ├─ &gt;=Max → EmitEscalated → OutputFailure (loud)
///     │                         └─ &lt;Max  → DispatchFixGeneration (llm-call) →
///     │                                      ExtractGenerateSuccess → GenerateSucceeded?
///     │                                        ├─ False → EmitGenerateFailed → OutputFailure
///     │                                        └─ True  → EmitGenerateSuccess → ApplyFixes
///     │                                                     ├─ Error → EmitApplyFailed → OutputFailure
///     │                                                     └─ Fixed → FixesApplied?
///     │                                                                  ├─ False → EmitApplyFailed → OutputFailure
///     │                                                                  └─ True  → EmitApplySuccess →
///     │                                                                             UpdateCodeIndex → OutputSuccess
///     └─ Error → EmitAnalyzeFailed → OutputFailure
/// </summary>
public class ReviewFixWorkflow : WorkflowBase
{
    /// <summary>
    /// Forward-installed cap on fix-generation attempts, mirroring PlanMaxRevisions /
    /// TaskMaxRevisions (&gt;=3) in SingleIssueCycleWorkflow and the merge-approval
    /// test-loop cap. NOTE: this cap is scaffolding — the verify→regenerate back-edge
    /// it would bound does not exist yet (deferred to Epic 38), so today the counter
    /// only reaches 1 and the <c>&gt;=MaxIterations</c> check never trips at runtime.
    /// It becomes the live loop bound once the verify loop-back is wired.
    /// </summary>
    // TODO(Epic 38): wire the verify→regenerate back-edge (failed verification →
    // IncrementIteration) that this cap is meant to bound; until then there is no loop.
    private const int MaxIterations = 3;

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Review Fix";
        builder.DefinitionId = "review-fix";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Analyze PR review comments and apply AI-generated fixes — graph-enforced loop bound, explicit error path, REVIEW_FIX.* audit events";

        // Fail-closed on internal fault (mirror MergeApprovalWorkflow SECURITY I1):
        // a faulted activity must not halt the instance with an incident and produce
        // NO outputs (which would let a caller read a stale/absent success). Continue
        // with incidents + seed success=false up front so a fault that stops the flow
        // before a terminal still yields a parseable, fail-closed result.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var repositoryVar = builder.WithVariable<string>("Repository", "");
        var branchNameVar = builder.WithVariable<string>("BranchName", "");
        var prNumberVar = builder.WithVariable<int>("PrNumber", 0);
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0);
        var tenantIdVar = builder.WithVariable<string>("TenantId", "");

        var hasActionableVar = builder.WithVariable<bool>("HasActionable", false);
        var analysisJsonVar = builder.WithVariable<string>("AnalysisJson", "");
        var fixesAppliedVar = builder.WithVariable<bool>("FixesApplied", false);
        var generateSucceededVar = builder.WithVariable<bool>("GenerateSucceeded", false);
        var iterationVar = builder.WithVariable<int>("Iteration", 0);
        var fixResultVar = builder.WithVariable<ReviewFixResult?>();
        var llmResultVar = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 0. Seed the fail-closed default outcome (success=false)
        // ================================================================
        var defaultOutcome = new SetOutput
        {
            Id = "DefaultOutcome",
            OutputName = new("success"),
            OutputValue = new(_ => (object)false),
        };
        defaultOutcome.SetDisplayText("Default Outcome = success:false");

        // ================================================================
        // 1. Read inputs into variables
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repositoryVar,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                branchNameVar.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                prNumberVar.Set(ctx, ctx.GetInput<int>("prNumber"));
                issueNumberVar.Set(ctx, ctx.GetInput<int>("issueNumber"));
                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Analyze review comments
        // ================================================================
        var analyze = new AnalyzeReviewActivity
        {
            Id = "AnalyzeReview", Name = "Analyze Review",
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            HasActionableComments = new Output<bool>(hasActionableVar),
            AnalysisJson = new Output<string?>(analysisJsonVar)
        };
        analyze.SetDisplayText("Analyze Review");

        var emitAnalyzeSuccess = EmitEvent(
            "EmitAnalyzeSuccess", "Emit REVIEW_FIX.ANALYZED.SUCCESS",
            ReviewFixEvents.AnalyzedSuccess, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            ctx => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["hasActionable"] = hasActionableVar.Get(ctx),
            }));

        var emitAnalyzeFailed = EmitEvent(
            "EmitAnalyzeFailed", "Emit REVIEW_FIX.ANALYZED.FAILED",
            ReviewFixEvents.AnalyzedFailed, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            _ => JsonSerializer.Serialize(new Dictionary<string, object?> { ["errorReason"] = "analysis_failed" }));

        var hasActionable = new FlowDecision(ctx => hasActionableVar.Get(ctx))
        { Id = "HasActionable", Name = "Has Actionable?" };
        hasActionable.SetDisplayText("Has Actionable?");

        // ================================================================
        // 3. Iteration-bound scaffolding — increment then cap-check BEFORE generating
        //    fixes. This is forward-installed for the verify→regenerate retry loop
        //    (deferred to Epic 38); with no back-edge today the counter only reaches 1,
        //    so the >=MaxIterations cap is structure, not a live runtime guard yet. The
        //    escalate path goes load-bearing once the verify loop-back lands.
        // ================================================================
        var incrementIteration = new SetVariable
        {
            Id = "IncrementIteration", Name = "Increment Iteration",
            Variable = iterationVar,
            Value = new Input<object?>(ctx => (object)(iterationVar.Get(ctx) + 1)),
        };
        incrementIteration.SetDisplayText("Increment Iteration");

        var maxIterations = new FlowDecision(ctx => iterationVar.Get(ctx) >= MaxIterations)
        { Id = "MaxIterations", Name = "Max Iterations?" };
        maxIterations.SetDisplayText("Max Iterations?");

        var emitEscalated = EmitEvent(
            "EmitEscalated", "Emit REVIEW_FIX.ESCALATED",
            ReviewFixEvents.Escalated, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            ctx => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["errorReason"] = "iteration_cap_exceeded",
                ["iterations"] = iterationVar.Get(ctx),
                ["maxIterations"] = MaxIterations,
            }));

        // ================================================================
        // 4. Generate fixes via the mediated llm-call workflow
        //    (DispatchFixGeneration id + developer/address-review-comments are
        //    asserted by the taxonomy-drift guard — keep them stable.)
        // ================================================================
        var generateFixes = new DispatchWorkflow
        {
            Id = "DispatchFixGeneration", Name = "Generate Fixes",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["agentRole"] = AgentRole.Developer.ToWire(),
                ["action"] = AgentAction.AddressReviewComments.ToWire(),
                ["taskPrompt"] = $"Apply fixes for the following review comments:\n{SecurityHelpers.SanitizeForPrompt(analysisJsonVar.Get(ctx))}",
                ["sessionId"] = $"adl-review-fix-{prNumberVar.Get(ctx)}",
                ["tenantId"] = tenantIdVar.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(llmResultVar)
        };
        generateFixes.SetDisplayText("Generate Fixes");

        // Read the dispatched llm-call's `success` output (CRITICAL — a failed
        // generation must NOT flow into apply as a false success).
        var extractGenerateSuccess = new SetVariable
        {
            Id = "ExtractGenerateSuccess", Name = "Extract Generate Success",
            Variable = generateSucceededVar,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResultVar.Get(ctx);
                if (result != null && result.TryGetValue("success", out var s))
                {
                    // Tolerant read (#15 sibling) — boxed bool / string / JsonElement, fail-closed.
                    return (object)ResumeInput.AsBool(s);
                }
                // No readable success flag → treat as a failed generation (never a
                // silent success).
                return (object)false;
            }),
        };
        extractGenerateSuccess.SetDisplayText("Extract Generate Success");

        var generateSucceeded = new FlowDecision(ctx => generateSucceededVar.Get(ctx))
        { Id = "GenerateSucceeded", Name = "Generate Succeeded?" };
        generateSucceeded.SetDisplayText("Generate Succeeded?");

        var emitGenerateSuccess = EmitEvent(
            "EmitGenerateSuccess", "Emit REVIEW_FIX.GENERATED.SUCCESS",
            ReviewFixEvents.GeneratedSuccess, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            ctx => JsonSerializer.Serialize(BuildLlmUsageData(llmResultVar.Get(ctx))));

        var emitGenerateFailed = EmitEvent(
            "EmitGenerateFailed", "Emit REVIEW_FIX.GENERATED.FAILED",
            ReviewFixEvents.GeneratedFailed, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            _ => JsonSerializer.Serialize(new Dictionary<string, object?> { ["errorReason"] = "fix_generation_failed" }));

        // ================================================================
        // 5. Apply the generated fix (LLM response provided by the dispatch above —
        //    no direct-LLM fallback is exercised here; removing that fallback from
        //    the activity is 32-5 T6's job).
        // ================================================================
        var applyFixes = new ApplyReviewFixesActivity
        {
            Id = "ApplyFixes", Name = "Apply Fixes",
            AnalysisJson = new Input<string>(ctx => analysisJsonVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            BranchName = new Input<string>(ctx => branchNameVar.Get(ctx)),
            LlmFixResponse = new Input<string?>(ctx =>
            {
                var result = llmResultVar.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var resp))
                    return resp?.ToString();
                return null;
            }),
            FixesApplied = new Output<bool>(fixesAppliedVar),
            FixResult = new Output<ReviewFixResult?>(fixResultVar)
        };
        applyFixes.SetDisplayText("Apply Fixes");

        // A "Fixed" outcome is gated on whether files were actually applied — a
        // Fixed-but-fixesApplied=false response is NOT a silent success.
        var fixesAppliedCheck = new FlowDecision(ctx => fixesAppliedVar.Get(ctx))
        { Id = "FixesApplied", Name = "Fixes Applied?" };
        fixesAppliedCheck.SetDisplayText("Fixes Applied?");

        var emitApplySuccess = EmitEvent(
            "EmitApplySuccess", "Emit REVIEW_FIX.APPLIED.SUCCESS",
            ReviewFixEvents.AppliedSuccess, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            ctx =>
            {
                var fixResult = fixResultVar.Get(ctx);
                return JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["filesFixed"] = fixResult?.FilesFixed?.Count ?? 0,
                });
            });

        var emitApplyFailed = EmitEvent(
            "EmitApplyFailed", "Emit REVIEW_FIX.APPLIED.FAILED",
            ReviewFixEvents.AppliedFailed, repositoryVar, prNumberVar, issueNumberVar, tenantIdVar,
            ctx =>
            {
                var fixResult = fixResultVar.Get(ctx);
                return JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["errorReason"] = "fix_apply_failed",
                    ["error"] = fixResult?.ErrorMessage ?? "",
                });
            });

        // ================================================================
        // 6. Update the code index for the changed files (best-effort indexer POST)
        // ================================================================
        var updateCodeIndex = new UpdateCodeIndexActivity
        {
            Id = "UpdateCodeIndex", Name = "Update Code Index",
            ChangedFilesJson = new Input<string?>(ctx =>
            {
                var fixResult = fixResultVar.Get(ctx);
                if (fixResult?.FilesFixed != null && fixResult.FilesFixed.Count > 0)
                    return JsonSerializer.Serialize(fixResult.FilesFixed);
                return null;
            }),
            RepositoryPath = new Input<string?>(ctx => repositoryVar.Get(ctx))
        };
        updateCodeIndex.SetDisplayText("Update Code Index");

        // ================================================================
        // 7. Terminals — distinct success / failure output sequences. `success`
        //    reflects reality (never a constant true), and the failure terminal
        //    surfaces an errorReason.
        // ================================================================
        var outputSuccess = new Sequence
        {
            Id = "OutputSuccess", Name = "Output Success",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSuccess_Success", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success=true"),
                WithLabel(new SetOutput { Id = "OutputSuccess_HasComments", OutputName = new("hasComments"), OutputValue = new(ctx => (object)hasActionableVar.Get(ctx)) }, "Output hasComments"),
                WithLabel(new SetOutput { Id = "OutputSuccess_FixesApplied", OutputName = new("fixesApplied"), OutputValue = new(ctx => (object)fixesAppliedVar.Get(ctx)) }, "Output fixesApplied"),
                WithLabel(new SetOutput { Id = "OutputSuccess_FilesFixed", OutputName = new("filesFixedCount"), OutputValue = new(ctx => (object)(fixResultVar.Get(ctx)?.FilesFixed?.Count ?? 0)) }, "Output filesFixedCount"),
            }
        };
        outputSuccess.SetDisplayText("Output Success");

        var outputFailure = new Sequence
        {
            Id = "OutputFailure", Name = "Output Failure",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputFailure_Success", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success=false"),
                WithLabel(new SetOutput { Id = "OutputFailure_HasComments", OutputName = new("hasComments"), OutputValue = new(ctx => (object)hasActionableVar.Get(ctx)) }, "Output hasComments"),
                WithLabel(new SetOutput { Id = "OutputFailure_FixesApplied", OutputName = new("fixesApplied"), OutputValue = new(ctx => (object)fixesAppliedVar.Get(ctx)) }, "Output fixesApplied"),
                WithLabel(new SetOutput { Id = "OutputFailure_ErrorReason", OutputName = new("errorReason"), OutputValue = new(_ => (object)"review_fix_failed") }, "Output errorReason"),
            }
        };
        outputFailure.SetDisplayText("Output Failure");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart — every outcome routed to a terminal, no dangling edge / deadlock
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "ReviewFixFlowchart",
            Name = "Review Fix Flowchart",
            Start = defaultOutcome,
            Activities =
            {
                defaultOutcome, readInputs, analyze,
                emitAnalyzeSuccess, emitAnalyzeFailed, hasActionable,
                incrementIteration, maxIterations, emitEscalated,
                generateFixes, extractGenerateSuccess, generateSucceeded,
                emitGenerateSuccess, emitGenerateFailed,
                applyFixes, fixesAppliedCheck, emitApplySuccess, emitApplyFailed,
                updateCodeIndex,
                outputSuccess, outputFailure, finish,
            },
            Connections =
            {
                Connect(defaultOutcome, readInputs),
                Connect(readInputs, analyze),

                // ── Analyze ──
                ConnectOutcome(analyze, "Done", emitAnalyzeSuccess),
                Connect(emitAnalyzeSuccess, hasActionable),
                ConnectOutcome(analyze, "Error", emitAnalyzeFailed),
                Connect(emitAnalyzeFailed, outputFailure),

                // ── No actionable comments → genuine success (nothing to fix) ──
                ConnectOutcome(hasActionable, "False", outputSuccess),

                // ── Actionable → iteration-bound scaffolding (forward-only today) ──
                ConnectOutcome(hasActionable, "True", incrementIteration),
                Connect(incrementIteration, maxIterations),
                ConnectOutcome(maxIterations, "True", emitEscalated),   // over cap → loud escalate (only reachable once the Epic-38 verify back-edge exists)
                Connect(emitEscalated, outputFailure),
                ConnectOutcome(maxIterations, "False", generateFixes),  // under cap → generate (always taken today: counter==1)

                // ── Generate fixes → read success → branch ──
                Connect(generateFixes, extractGenerateSuccess),
                Connect(extractGenerateSuccess, generateSucceeded),
                ConnectOutcome(generateSucceeded, "False", emitGenerateFailed),
                Connect(emitGenerateFailed, outputFailure),
                ConnectOutcome(generateSucceeded, "True", emitGenerateSuccess),
                Connect(emitGenerateSuccess, applyFixes),

                // ── Apply fixes → fail-closed branch ──
                ConnectOutcome(applyFixes, "Error", emitApplyFailed),
                ConnectOutcome(applyFixes, "Fixed", fixesAppliedCheck),
                ConnectOutcome(fixesAppliedCheck, "False", emitApplyFailed),
                Connect(emitApplyFailed, outputFailure),
                ConnectOutcome(fixesAppliedCheck, "True", emitApplySuccess),
                Connect(emitApplySuccess, updateCodeIndex),
                Connect(updateCodeIndex, outputSuccess),

                // ── Terminals → finish ──
                Connect(outputSuccess, finish),
                Connect(outputFailure, finish),
            }
        };
    }

    /// <summary>
    /// A <c>REVIEW_FIX.*</c> event-emit node carrying the repo / pr / issue / tenant
    /// context plus a per-edge JSON data payload.
    /// </summary>
    private static EmitReviewFixEventActivity EmitEvent(
        string id, string label, string eventType,
        Variable<string> repository, Variable<int> prNumber, Variable<int> issueNumber,
        Variable<string> tenantId,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string?> dataJson)
    {
        var emit = new EmitReviewFixEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(_ => eventType),
            Repository = new Input<string?>(ctx => repository.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumber.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            DataJson = new Input<string?>(dataJson),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    /// <summary>
    /// Extract the provider / cost / token usage the dispatched <c>llm-call</c>
    /// surfaces on its result dictionary, for the GENERATED.SUCCESS event payload.
    /// </summary>
    private static Dictionary<string, object?> BuildLlmUsageData(IDictionary<string, object>? llmResult)
    {
        var data = new Dictionary<string, object?>();
        if (llmResult == null) return data;
        if (llmResult.TryGetValue("providerUsed", out var p)) data["provider"] = p?.ToString();
        if (llmResult.TryGetValue("costUsd", out var c)) data["costUsd"] = c;
        if (llmResult.TryGetValue("tokensUsed", out var t)) data["tokensUsed"] = t;
        return data;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
