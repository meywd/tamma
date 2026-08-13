using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Blocker;
using Tamma.Activities.Blocker.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using Tamma.Activities.Security;
using Tamma.Api.Services.Agents;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Blocker Diagnosis Sub-Workflow (Story 7-1G).
///
/// Collects signals in parallel (git, CI, inactivity, communication),
/// uses AI to diagnose blocker type (8 categories) and severity,
/// then executes progressive resolution:
///   Level 1: Hint (Socratic) -- 15min wait (30min for skill 4-5; skipped for skill 1-2)
///   Level 2: Guidance -- 30min wait
///   Level 3: Assistance -- 45min wait
///   Level 4: Escalation -- wait for senior (durable SLA timeout)
///
/// Can be invoked standalone via ELSA REST API or as a child workflow via DispatchWorkflow.
///
/// Design: Flowchart with visible nodes for each phase in ELSA Studio.
///
/// <para><b>Completeness build-out 2026-06-22 (BlockerDiagnosis.md, 7-1G AC2/AC6/AC9).</b>
/// This pass fixes the P0/P1 correctness + observability gaps:
///   - The terminal status now reflects the ACTUAL ladder outcome: Resolved (progress
///     detected, or a senior resolved the escalation) → else Timeout (escalation SLA
///     expired with no senior response) → else Escalated (senior notified, awaiting).
///     The old graph always reported "Escalated" and never produced the Timeout terminal.
///   - Progress detected at any level short-circuits the ladder (the shared !isResolved
///     guard) and emits a terminal RESOLVED event immediately.
///   - Per-level waits and the escalation SLA are now durable timeouts (no hang-forever),
///     enforced inside DetectProgressActivity / EscalateToSeniorActivity via the DelayFor
///     (Delay) bookmark — EF-persisted and re-armed by Elsa.Scheduling after a host restart
///     (survives a VPS restart mid-wait). Wait times moved to BlockerDiagnosis:* config.
///   - Every rung emits a BLOCKER.* DCB audit event (diagnosed / resolution attempted /
///     progress detected / progress timed-out / escalated / resolved / timed-out) tagged
///     with sessionId/storyId/juniorId/tenantId/level/blockerType, plus the AC9 OTel
///     metrics (blocker.total / resolved / escalated / timed_out / resolution_time).
///   - tenantId is threaded into every llm-call and escalation (Epic 32 tenant-scoping).
/// 7-11 (full prompt-enrichment: story title/description/expected-files/conventions/
/// resolution-history threading) remains a noted follow-up — out of scope for this
/// correctness pass.</para>
///
/// <para><b>Reachability of the <c>Resolved</c> terminal (follow-up #15 — DONE).</b> The
/// in-graph wiring for <c>Resolved</c> is correct: a <c>ProgressDetected</c> /
/// <c>Resolved</c> / <c>SeniorResponse</c> resume at any level flips <c>isResolved</c> and
/// short-circuits the ladder. The generic resumer
/// (<c>MentorshipController.ResumeSession</c> → <c>ElsaWorkflowService.ResumeWorkflowAsync</c>)
/// still hits Elsa's generic <c>/resume</c> with NO bookmark id and NO input, so it cannot
/// supply those keys — but the blocker-specific resume endpoint that DOES now exists:
/// <c>POST /api/adl/blocker/resume</c> (<c>AdlEndpoints.ResumeBlocker</c>) →
/// <c>ElsaWorkflowService.ResumeBlockerResolutionAsync</c> → the engine seam
/// <c>BlockerResumeEndpoint</c>. It targets the progress
/// (<c>blocker-progress-{session}-{level}</c>) / escalation (<c>blocker-escalation-{session}</c>)
/// bookmark and injects the <c>ProgressDetected</c> / <c>Resolved</c> / <c>SeniorResponse</c>
/// input, mirroring the secure <c>MergeApprovalResumeEndpoint</c> (WorkflowsManage RBAC;
/// server-derived resolver for I2; tenant-ownership check on the session so a cross-tenant
/// resume 404s and never acts). A production run can therefore now reach the <c>Resolved</c>
/// terminal, not only <c>Escalated</c> / (durable) <c>Timeout</c>.</para>
/// </summary>
public class BlockerDiagnosisWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Blocker Diagnosis";
        builder.DefinitionId = "blocker-diagnosis";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Diagnoses blocker type and applies progressive resolution (hint -> guidance -> assistance -> escalation)";

        // ============================================
        // Workflow Variables
        // ============================================
        var sessionId = builder.WithVariable<Guid>().Persisted();
        var storyId = builder.WithVariable<string>().Persisted();
        var juniorId = builder.WithVariable<string>().Persisted();
        var tenantId = builder.WithVariable<string>().Persisted();
        var skillLevel = builder.WithVariable<int>().Persisted();
        var blockerContext = builder.WithVariable<string?>().Persisted();
        var repository = builder.WithVariable<string>().Persisted();
        var branchName = builder.WithVariable<string>().Persisted();

        // Signal variables
        var gitSignal = builder.WithVariable<GitActivitySignal>().Persisted();
        var ciSignal = builder.WithVariable<CIStatusSignal>().Persisted();
        var inactivitySignal = builder.WithVariable<InactivitySignal>().Persisted();
        var communicationSignal = builder.WithVariable<CommunicationSignal>().Persisted();
        var aggregatedSignals = builder.WithVariable<AggregatedSignals>().Persisted();

        // Diagnosis variables
        var llmDiagnosisOutput = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var diagnosisResult = builder.WithVariable<BlockerDiagnosisResult>().Persisted();

        // Resolution tracking
        var currentLevel = builder.WithVariable<string>("Hint").Persisted();
        var attempts = builder.WithVariable<int>(0).Persisted();
        var feedbackProvided = builder.WithVariable<List<string>>().Persisted();
        var startTime = builder.WithVariable<DateTime>().Persisted();
        var isResolved = builder.WithVariable<bool>(false).Persisted();
        var progressDetected = builder.WithVariable<bool>(false).Persisted();
        var progressTimedOut = builder.WithVariable<bool>(false).Persisted();
        var progressResult = builder.WithVariable<ProgressDetectionResult>().Persisted();
        // Escalation SLA expired with no senior response → terminal Timeout (7-1G AC2).
        var timedOut = builder.WithVariable<bool>(false).Persisted();

        // ============================================
        // Activities
        // ============================================

        // 1. Capture Inputs
        var captureInputs = new SetVariable
        {
            Id = "CaptureInputs",
            Name = "Capture Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                var sid = context.GetInput<Guid>("sessionId");
                storyId.Set(context, context.GetInput<string>("storyId") ?? "");
                juniorId.Set(context, context.GetInput<string>("juniorId") ?? "");
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? "");
                skillLevel.Set(context, Math.Max(1, context.GetInput<int>("skillLevel")));
                blockerContext.Set(context, context.GetInput<string?>("blockerContext"));
                repository.Set(context, context.GetInput<string>("repository") ?? "");
                branchName.Set(context, context.GetInput<string>("branchName") ?? $"feature/{context.GetInput<string>("storyId") ?? ""}");
                startTime.Set(context, DateTime.UtcNow);
                feedbackProvided.Set(context, new List<string>());
                return sid;
            })
        };
        captureInputs.SetDisplayText("Capture Inputs");

        // 2. Parallel Signal Collection
        var parallelSignals = new Elsa.Workflows.Activities.Parallel
        {
            Id = "ParallelSignals",
            Name = "Collect Signals",
            Activities =
            {
                WithLabel(new CollectGitActivityActivity
                {
                    Id = "CollectGit",
                    Name = "Collect Git Activity",
                    Repository = new(context => repository.Get(context) ?? ""),
                    BranchName = new(context => branchName.Get(context) ?? ""),
                    Result = new(gitSignal)
                }, "Collect Git Activity"),
                WithLabel(new CollectCIStatusActivity
                {
                    Id = "CollectCI",
                    Name = "Collect CI Status",
                    Repository = new(context => repository.Get(context) ?? ""),
                    BranchName = new(context => branchName.Get(context) ?? ""),
                    Result = new(ciSignal)
                }, "Collect CI Status"),
                WithLabel(new CollectInactivityActivity
                {
                    Id = "CollectInactivity",
                    Name = "Collect Inactivity",
                    Repository = new(context => repository.Get(context) ?? ""),
                    BranchName = new(context => branchName.Get(context) ?? ""),
                    Result = new(inactivitySignal)
                }, "Collect Inactivity"),
                WithLabel(new CollectCommunicationActivity
                {
                    Id = "CollectComms",
                    Name = "Collect Communication",
                    JuniorId = new(context => juniorId.Get(context) ?? ""),
                    Result = new(communicationSignal)
                }, "Collect Communication")
            }
        };
        parallelSignals.SetDisplayText("Collect Signals");

        // 3. Aggregate Signals
        var aggregateSignals = new SetVariable
        {
            Id = "AggregateSignals",
            Name = "Aggregate Signals",
            Variable = aggregatedSignals,
            Value = new(context =>
            {
                var git = gitSignal.Get(context);
                var ci = ciSignal.Get(context);
                var inact = inactivitySignal.Get(context);
                var comms = communicationSignal.Get(context);

                var successCount = 0;
                if (git?.CollectionSucceeded == true) successCount++;
                if (ci?.CollectionSucceeded == true) successCount++;
                if (inact?.CollectionSucceeded == true) successCount++;
                if (comms?.CollectionSucceeded == true) successCount++;

                return new AggregatedSignals
                {
                    GitActivity = git,
                    CIStatus = ci,
                    Inactivity = inact,
                    Communication = comms,
                    CollectedAt = DateTime.UtcNow,
                    SuccessfulCollectors = successCount,
                    TotalCollectors = 4
                };
            })
        };
        aggregateSignals.SetDisplayText("Aggregate Signals");

        // 4. AI Diagnosis via LLM Call
        var aiDiagnosis = new DispatchWorkflow
        {
            Id = "AIDiagnosis",
            Name = "AI Diagnosis",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(context => new Dictionary<string, object>
            {
                ["role"] = AgentRole.SeniorDeveloper.ToWire(),
                ["action"] = AgentAction.ResolveBlocker.ToWire(),
                ["analysisType"] = "BlockerDiagnosis",
                ["content"] = BuildDiagnosisPrompt(
                    aggregatedSignals.Get(context),
                    skillLevel.Get(context),
                    blockerContext.Get(context)),
                ["sessionId"] = sessionId.Get(context),
                ["tenantId"] = tenantId.Get(context) ?? "",
                ["skillLevel"] = skillLevel.Get(context)
            }),
            WaitForCompletion = new(true),
            Result = new(llmDiagnosisOutput)
        };
        aiDiagnosis.SetDisplayText("AI Diagnosis");

        // 5. Classify Blocker
        var classifyBlocker = new ClassifyBlockerActivity
        {
            Id = "ClassifyBlocker",
            Name = "Classify Blocker",
            Signals = new(context => aggregatedSignals.Get(context) ?? new AggregatedSignals()),
            AIDiagnosisResponse = new(context => {
                var output = llmDiagnosisOutput.Get(context);
                if (output != null && output.TryGetValue("llmResponse", out var resp))
                    return resp?.ToString();
                return null;
            }),
            SkillLevel = new(context => skillLevel.Get(context)),
            BlockerContext = new(context => blockerContext.Get(context)),
            Result = new(diagnosisResult)
        };
        classifyBlocker.SetDisplayText("Classify Blocker");

        // 5b. Emit BLOCKER.DIAGNOSED.SUCCESS (audit + blocker.total metric)
        var emitDiagnosed = new EmitBlockerEventActivity
        {
            Id = "EmitDiagnosed",
            Name = "Emit: Diagnosed",
            EventType = new(BlockerEvents.DiagnosedSuccess),
            SessionId = new(context => sessionId.Get(context).ToString()),
            StoryId = new(context => storyId.Get(context) ?? ""),
            JuniorId = new(context => juniorId.Get(context) ?? ""),
            TenantId = new(context => tenantId.Get(context) ?? ""),
            BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? ""),
            Severity = new(context => diagnosisResult.Get(context)?.Severity.ToString() ?? ""),
            Confidence = new(context => diagnosisResult.Get(context)?.Confidence ?? 0d)
        };
        emitDiagnosed.SetDisplayText("Emit: Diagnosed");

        // 6. Determine Starting Level (Skill Adaptation)
        var determineStartLevel = new SetVariable
        {
            Id = "DetermineStartLevel",
            Name = "Determine Start Level",
            Variable = currentLevel,
            Value = new(context =>
            {
                var sl = skillLevel.Get(context);
                // Level 1-2: skip Hint (Socratic too frustrating for beginners)
                return sl <= 2 ? "Guidance" : "Hint";
            })
        };
        determineStartLevel.SetDisplayText("Determine Start Level");

        // 7a. Level 1: Hint
        var hintLevel = new Sequence
        {
            Id = "HintLevel",
            Name = "Level 1: Hint",
            Activities =
            {
                BuildHintLevel(sessionId, storyId, juniorId, tenantId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected,
                    progressTimedOut, progressResult)
            }
        };
        hintLevel.SetDisplayText("Level 1: Hint");

        // 7b. Level 2: Guidance
        var guidanceLevel = new Sequence
        {
            Id = "GuidanceLevel",
            Name = "Level 2: Guidance",
            Activities =
            {
                BuildGuidanceLevel(sessionId, storyId, juniorId, tenantId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected,
                    progressTimedOut, progressResult)
            }
        };
        guidanceLevel.SetDisplayText("Level 2: Guidance");

        // 7c. Level 3: Assistance
        var assistanceLevel = new Sequence
        {
            Id = "AssistanceLevel",
            Name = "Level 3: Assistance",
            Activities =
            {
                BuildAssistanceLevel(sessionId, storyId, juniorId, tenantId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected,
                    progressTimedOut, progressResult)
            }
        };
        assistanceLevel.SetDisplayText("Level 3: Assistance");

        // 7d. Level 4: Escalation
        var escalationLevel = new Sequence
        {
            Id = "EscalationLevel",
            Name = "Level 4: Escalation",
            Activities =
            {
                BuildEscalationLevel(sessionId, storyId, juniorId, tenantId, diagnosisResult,
                    aggregatedSignals, currentLevel, attempts, feedbackProvided, isResolved, timedOut)
            }
        };
        escalationLevel.SetDisplayText("Level 4: Escalation");

        // 8. Emit terminal BLOCKER event (RESOLVED / TIMED_OUT / ESCALATED) + terminal metric.
        var emitTerminal = new EmitBlockerEventActivity
        {
            Id = "EmitTerminal",
            Name = "Emit: Terminal",
            EventType = new(context => TerminalEventType(isResolved.Get(context), timedOut.Get(context))),
            SessionId = new(context => sessionId.Get(context).ToString()),
            StoryId = new(context => storyId.Get(context) ?? ""),
            JuniorId = new(context => juniorId.Get(context) ?? ""),
            TenantId = new(context => tenantId.Get(context) ?? ""),
            BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? ""),
            Severity = new(context => diagnosisResult.Get(context)?.Severity.ToString() ?? ""),
            Level = new(context => currentLevel.Get(context) ?? ""),
            Attempt = new(context => attempts.Get(context)),
            ResolutionTimeSeconds = new(context =>
                (DateTime.UtcNow - startTime.Get(context)).TotalSeconds)
        };
        emitTerminal.SetDisplayText("Emit: Terminal");

        // 9. Set Output
        var setOutput = new SetOutput
        {
            Id = "SetBlockerOutput",
            Name = "Output: Blocker Resolution",
            OutputName = new("BlockerResolution"),
            OutputValue = new(context =>
            {
                var diagnosis = diagnosisResult.Get(context);
                var start = startTime.Get(context);
                var resolutionTime = DateTime.UtcNow - start;

                return new BlockerResolution
                {
                    // Resolved → else Timeout (escalation SLA expired) → else Escalated.
                    // Fixes the always-"Escalated" bug and produces the real Timeout terminal.
                    Status = ResolveStatus(isResolved.Get(context), timedOut.Get(context)),
                    BlockerType = diagnosis?.BlockerType ?? BlockerCategory.TechnicalKnowledgeGap,
                    BlockerSeverity = diagnosis?.Severity ?? BlockerDiagnosisSeverity.Medium,
                    Attempts = attempts.Get(context),
                    ResolutionLevel = Enum.TryParse<ResolutionLevel>(currentLevel.Get(context), out var lvl)
                        ? lvl
                        : ResolutionLevel.Hint,
                    ResolutionTime = resolutionTime,
                    DiagnosisDetails = diagnosis?.RootCauseHypothesis ?? "",
                    FeedbackProvided = feedbackProvided.Get(context) ?? new List<string>()
                };
            })
        };
        setOutput.SetDisplayText("Output: Blocker Resolution");

        // ============================================
        // Flowchart
        // ============================================
        builder.Root = new Flowchart
        {
            Id = "BlockerDiagnosisFlowchart",
            Start = captureInputs,
            Activities =
            {
                captureInputs, parallelSignals, aggregateSignals, aiDiagnosis,
                classifyBlocker, emitDiagnosed, determineStartLevel,
                hintLevel, guidanceLevel, assistanceLevel, escalationLevel,
                emitTerminal, setOutput
            },
            Connections =
            {
                Connect(captureInputs, parallelSignals),
                Connect(parallelSignals, aggregateSignals),
                Connect(aggregateSignals, aiDiagnosis),
                Connect(aiDiagnosis, classifyBlocker),
                Connect(classifyBlocker, emitDiagnosed),
                Connect(emitDiagnosed, determineStartLevel),
                Connect(determineStartLevel, hintLevel),
                Connect(hintLevel, guidanceLevel),
                Connect(guidanceLevel, assistanceLevel),
                Connect(assistanceLevel, escalationLevel),
                Connect(escalationLevel, emitTerminal),
                Connect(emitTerminal, setOutput)
            }
        };
    }

    // ================================================================
    // Terminal status / event helpers (pure — exposed for unit testing)
    // ================================================================

    /// <summary>
    /// Terminal status precedence (7-1G AC2/AC6): a resolved blocker → Resolved; otherwise an
    /// escalation whose SLA expired with no senior response → Timeout; otherwise Escalated
    /// (senior notified, awaiting). NEVER reports Resolved/Escalated inaccurately.
    /// </summary>
    internal static BlockerResolutionStatus ResolveStatus(bool isResolved, bool timedOut)
        => isResolved
            ? BlockerResolutionStatus.Resolved
            : timedOut
                ? BlockerResolutionStatus.Timeout
                : BlockerResolutionStatus.Escalated;

    /// <summary>Map the terminal status onto its BLOCKER.* event type.</summary>
    internal static string TerminalEventType(bool isResolved, bool timedOut)
        => isResolved
            ? BlockerEvents.Resolved
            : timedOut
                ? BlockerEvents.TimedOut
                : BlockerEvents.Escalated;

    // ================================================================
    // Flowchart helpers
    // ================================================================

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

    /// <summary>
    /// Builds the per-level "record this attempt's feedback" SetVariable plus the
    /// BLOCKER.RESOLUTION_ATTEMPTED emit. Kept as a small helper so each level shares the
    /// identical attempt-counter + audit semantics.
    /// </summary>
    private static EmitBlockerEventActivity BuildResolutionAttemptedEmit(
        string idPrefix,
        string level,
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<string> tenantId,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<int> attempts)
        => new()
        {
            Id = $"{idPrefix}EmitAttempt",
            Name = $"Emit: {level} Attempt",
            EventType = new(BlockerEvents.ResolutionAttempted),
            SessionId = new(context => sessionId.Get(context).ToString()),
            StoryId = new(context => storyId.Get(context) ?? ""),
            JuniorId = new(context => juniorId.Get(context) ?? ""),
            TenantId = new(context => tenantId.Get(context) ?? ""),
            BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? ""),
            Severity = new(context => diagnosisResult.Get(context)?.Severity.ToString() ?? ""),
            Level = new(level),
            Attempt = new(context => attempts.Get(context))
        };

    /// <summary>
    /// Builds the per-level "progress detected / timed-out" emit. Emits
    /// BLOCKER.PROGRESS_DETECTED when the junior made progress, else
    /// BLOCKER.PROGRESS_TIMED_OUT (the wait expired and the ladder advanced).
    /// </summary>
    private static EmitBlockerEventActivity BuildProgressEmit(
        string idPrefix,
        string level,
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<string> tenantId,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<bool> progressDetected,
        Variable<ProgressDetectionResult> progressResult)
        => new()
        {
            Id = $"{idPrefix}EmitProgress",
            Name = $"Emit: {level} Progress",
            EventType = new(context => progressDetected.Get(context)
                ? BlockerEvents.ProgressDetected
                : BlockerEvents.ProgressTimedOut),
            SessionId = new(context => sessionId.Get(context).ToString()),
            StoryId = new(context => storyId.Get(context) ?? ""),
            JuniorId = new(context => juniorId.Get(context) ?? ""),
            TenantId = new(context => tenantId.Get(context) ?? ""),
            BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? ""),
            Level = new(level),
            ProgressType = new(context => progressResult.Get(context)?.ProgressType ?? "")
        };

    /// <summary>
    /// Level 1: Hint (Socratic Method).
    /// Skipped for skill level 1-2. Extended timeout (config) for skill 4-5.
    /// </summary>
    private static If BuildHintLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<string> tenantId,
        Variable<int> skillLevel,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> progressDetected,
        Variable<bool> progressTimedOut,
        Variable<ProgressDetectionResult> progressResult)
    {
        var hintBody = WithLabel(new Sequence
        {
            Id = "HintBody",
            Name = "Hint Body",
            Activities =
            {
                    WithLabel(new DispatchWorkflow
                    {
                        Id = "HintLlmCall",
                        Name = "Hint LLM Call",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(context => new Dictionary<string, object>
                        {
                            ["role"] = AgentRole.SeniorDeveloper.ToWire(),
                            ["action"] = AgentAction.MentorFeedback.ToWire(),
                            ["analysisType"] = "GuidanceGeneration",
                            ["content"] = $"Provide Socratic hints for: {SecurityHelpers.SanitizeForPrompt(diagnosisResult.Get(context)?.RootCauseHypothesis ?? "unknown blocker")}. " +
                                          $"Blocker type: {diagnosisResult.Get(context)?.BlockerType}. " +
                                          "Use guiding questions, not direct answers. Employ the Socratic method.",
                            ["sessionId"] = sessionId.Get(context),
                            ["tenantId"] = tenantId.Get(context) ?? "",
                            ["skillLevel"] = skillLevel.Get(context)
                        }),
                        WaitForCompletion = new(true)
                    }, "Hint LLM Call"),

                    WithLabel(new SetVariable
                    {
                        Id = "HintRecordFeedback",
                        Name = "Record Hint Feedback",
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { $"[Hint] Socratic hints provided for {diagnosisResult.Get(context)?.BlockerType}" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    }, "Record Hint Feedback"),

                    WithLabel(BuildResolutionAttemptedEmit("Hint", "Hint", sessionId, storyId, juniorId, tenantId, diagnosisResult, attempts),
                        "Emit: Hint Attempt"),

                    // Durable wait: bookmark + scheduled timeout (WaitTimeMinutes=0 → config/defaults).
                    WithLabel(new DetectProgressActivity
                    {
                        Id = "HintDetectProgress",
                        Name = "Hint: Detect Progress",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Hint"),
                        WaitTimeMinutes = new(0),
                        ProgressDetected = new(progressDetected),
                        TimedOut = new(progressTimedOut),
                        Result = new(progressResult)
                    }, "Hint: Detect Progress"),

                    WithLabel(new SetVariable
                    {
                        Id = "HintCheckProgress",
                        Name = "Hint: Check Progress",
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var detected = progressDetected.Get(context);
                            if (!detected)
                                currentLevel.Set(context, "Guidance");
                            return detected;
                        })
                    }, "Hint: Check Progress"),

                    WithLabel(BuildProgressEmit("Hint", "Hint", sessionId, storyId, juniorId, tenantId, diagnosisResult, progressDetected, progressResult),
                        "Emit: Hint Progress")
                }
            }, "Hint Body");

        var hintIf = new If
        {
            Id = "HintCondition",
            Name = "Hint Applicable?",
            Condition = new(context =>
                currentLevel.Get(context) == "Hint" && !isResolved.Get(context)),
            Then = hintBody
        };
        hintIf.SetDisplayText("Hint Applicable?");
        return hintIf;
    }

    /// <summary>
    /// Level 2: Direct Guidance.
    /// </summary>
    private static If BuildGuidanceLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<string> tenantId,
        Variable<int> skillLevel,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> progressDetected,
        Variable<bool> progressTimedOut,
        Variable<ProgressDetectionResult> progressResult)
    {
        var guidanceBody = WithLabel(new Sequence
        {
            Id = "GuidanceBody",
            Name = "Guidance Body",
                Activities =
                {
                    WithLabel(new SetVariable
                    {
                        Id = "SetLevelGuidance",
                        Name = "Set Level: Guidance",
                        Variable = currentLevel,
                        Value = new(context => "Guidance")
                    }, "Set Level: Guidance"),

                    WithLabel(new DispatchWorkflow
                    {
                        Id = "GuidanceLlmCall",
                        Name = "Guidance LLM Call",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(context => new Dictionary<string, object>
                        {
                            ["role"] = AgentRole.SeniorDeveloper.ToWire(),
                            ["action"] = AgentAction.MentorFeedback.ToWire(),
                            ["analysisType"] = "GuidanceGeneration",
                            ["content"] = $"Provide direct guidance for: {SecurityHelpers.SanitizeForPrompt(diagnosisResult.Get(context)?.RootCauseHypothesis ?? "unknown blocker")}. " +
                                          $"Blocker type: {diagnosisResult.Get(context)?.BlockerType}. " +
                                          "Give clear, step-by-step instructions. Be specific and actionable.",
                            ["sessionId"] = sessionId.Get(context),
                            ["tenantId"] = tenantId.Get(context) ?? "",
                            ["skillLevel"] = skillLevel.Get(context)
                        }),
                        WaitForCompletion = new(true)
                    }, "Guidance LLM Call"),

                    WithLabel(new SetVariable
                    {
                        Id = "GuidanceRecordFeedback",
                        Name = "Record Guidance Feedback",
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { $"[Guidance] Direct guidance provided for {diagnosisResult.Get(context)?.BlockerType}" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    }, "Record Guidance Feedback"),

                    WithLabel(BuildResolutionAttemptedEmit("Guidance", "Guidance", sessionId, storyId, juniorId, tenantId, diagnosisResult, attempts),
                        "Emit: Guidance Attempt"),

                    WithLabel(new DetectProgressActivity
                    {
                        Id = "GuidanceDetectProgress",
                        Name = "Guidance: Detect Progress",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Guidance"),
                        WaitTimeMinutes = new(0),
                        ProgressDetected = new(progressDetected),
                        TimedOut = new(progressTimedOut),
                        Result = new(progressResult)
                    }, "Guidance: Detect Progress"),

                    WithLabel(new SetVariable
                    {
                        Id = "GuidanceCheckProgress",
                        Name = "Guidance: Check Progress",
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var detected = progressDetected.Get(context);
                            if (!detected)
                                currentLevel.Set(context, "Assistance");
                            return detected;
                        })
                    }, "Guidance: Check Progress"),

                    WithLabel(BuildProgressEmit("Guidance", "Guidance", sessionId, storyId, juniorId, tenantId, diagnosisResult, progressDetected, progressResult),
                        "Emit: Guidance Progress")
                }
            }, "Guidance Body");

        var guidanceIf = new If
        {
            Id = "GuidanceCondition",
            Name = "Guidance Applicable?",
            Condition = new(context => !isResolved.Get(context)),
            Then = guidanceBody
        };
        guidanceIf.SetDisplayText("Guidance Applicable?");
        return guidanceIf;
    }

    /// <summary>
    /// Level 3: Code Assistance. Uses the developer role (implement-fix) to produce
    /// working solution code.
    /// </summary>
    private static If BuildAssistanceLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<string> tenantId,
        Variable<int> skillLevel,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> progressDetected,
        Variable<bool> progressTimedOut,
        Variable<ProgressDetectionResult> progressResult)
    {
        var assistanceBody = WithLabel(new Sequence
        {
            Id = "AssistanceBody",
            Name = "Assistance Body",
                Activities =
                {
                    WithLabel(new SetVariable
                    {
                        Id = "SetLevelAssistance",
                        Name = "Set Level: Assistance",
                        Variable = currentLevel,
                        Value = new(context => "Assistance")
                    }, "Set Level: Assistance"),

                    WithLabel(new DispatchWorkflow
                    {
                        Id = "AssistanceLlmCall",
                        Name = "Assistance LLM Call",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(context => new Dictionary<string, object>
                        {
                            ["role"] = AgentRole.Developer.ToWire(),
                            ["action"] = AgentAction.ImplementFix.ToWire(),
                            ["analysisType"] = "GuidanceGeneration",
                            ["content"] = $"Provide code example for: {SecurityHelpers.SanitizeForPrompt(diagnosisResult.Get(context)?.RootCauseHypothesis ?? "unknown blocker")}. " +
                                          $"Blocker type: {diagnosisResult.Get(context)?.BlockerType}. " +
                                          "Include a working code example with detailed explanation. " +
                                          "Show the solution step by step.",
                            ["sessionId"] = sessionId.Get(context),
                            ["tenantId"] = tenantId.Get(context) ?? "",
                            ["skillLevel"] = skillLevel.Get(context)
                        }),
                        WaitForCompletion = new(true)
                    }, "Assistance LLM Call"),

                    WithLabel(new SetVariable
                    {
                        Id = "AssistanceRecordFeedback",
                        Name = "Record Assistance Feedback",
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { $"[Assistance] Code example provided for {diagnosisResult.Get(context)?.BlockerType}" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    }, "Record Assistance Feedback"),

                    WithLabel(BuildResolutionAttemptedEmit("Assistance", "Assistance", sessionId, storyId, juniorId, tenantId, diagnosisResult, attempts),
                        "Emit: Assistance Attempt"),

                    WithLabel(new DetectProgressActivity
                    {
                        Id = "AssistanceDetectProgress",
                        Name = "Assistance: Detect Progress",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Assistance"),
                        WaitTimeMinutes = new(0),
                        ProgressDetected = new(progressDetected),
                        TimedOut = new(progressTimedOut),
                        Result = new(progressResult)
                    }, "Assistance: Detect Progress"),

                    WithLabel(new SetVariable
                    {
                        Id = "AssistanceCheckProgress",
                        Name = "Assistance: Check Progress",
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var detected = progressDetected.Get(context);
                            if (!detected)
                                currentLevel.Set(context, "Escalation");
                            return detected;
                        })
                    }, "Assistance: Check Progress"),

                    WithLabel(BuildProgressEmit("Assistance", "Assistance", sessionId, storyId, juniorId, tenantId, diagnosisResult, progressDetected, progressResult),
                        "Emit: Assistance Progress")
                }
            }, "Assistance Body");

        var assistanceIf = new If
        {
            Id = "AssistanceCondition",
            Name = "Assistance Applicable?",
            Condition = new(context => !isResolved.Get(context)),
            Then = assistanceBody
        };
        assistanceIf.SetDisplayText("Assistance Applicable?");
        return assistanceIf;
    }

    /// <summary>
    /// Level 4: Senior Escalation. Compiles context dump, notifies senior, waits via
    /// bookmark with a durable SLA timeout. A senior who RESOLVES the escalation flips
    /// isResolved (terminal Resolved at Escalation); an expired SLA flips timedOut
    /// (terminal Timeout). Fixes the always-"Escalated" bug.
    /// </summary>
    private static If BuildEscalationLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<string> tenantId,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<AggregatedSignals> aggregatedSignals,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> timedOut)
    {
        // Local outputs from the escalation activity, fed back into isResolved / timedOut.
        var escalationResolved = new Variable<bool>();
        var escalationTimedOut = new Variable<bool>();

        var escalationBody = WithLabel(new Sequence
        {
            Id = "EscalationBody",
            Name = "Escalation Body",
            Variables = { escalationResolved, escalationTimedOut },
                Activities =
                {
                    WithLabel(new SetVariable
                    {
                        Id = "SetLevelEscalation",
                        Name = "Set Level: Escalation",
                        Variable = currentLevel,
                        Value = new(context => "Escalation")
                    }, "Set Level: Escalation"),

                    WithLabel(BuildResolutionAttemptedEmit("Escalation", "Escalation", sessionId, storyId, juniorId, tenantId, diagnosisResult, attempts),
                        "Emit: Escalation Attempt"),

                    // Emit BLOCKER.ESCALATED before the (suspending) wait so the audit row
                    // lands even if the workflow then suspends awaiting the senior.
                    WithLabel(new EmitBlockerEventActivity
                    {
                        Id = "EmitEscalated",
                        Name = "Emit: Escalated",
                        EventType = new(BlockerEvents.Escalated),
                        SessionId = new(context => sessionId.Get(context).ToString()),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        TenantId = new(context => tenantId.Get(context) ?? ""),
                        BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? ""),
                        Severity = new(context => diagnosisResult.Get(context)?.Severity.ToString() ?? ""),
                        Level = new("Escalation")
                    }, "Emit: Escalated"),

                    WithLabel(new EscalateToSeniorActivity
                    {
                        Id = "EscalateToSenior",
                        Name = "Escalate to Senior",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? "TechnicalKnowledgeGap"),
                        BlockerSeverity = new(context => diagnosisResult.Get(context)?.Severity.ToString() ?? "High"),
                        DiagnosisDetails = new(context => diagnosisResult.Get(context)?.RootCauseHypothesis ?? ""),
                        PreviousAttempts = new(context => feedbackProvided.Get(context) ?? new List<string>()),
                        Signals = new(context => aggregatedSignals.Get(context)),
                        Resolved = new(escalationResolved),
                        TimedOut = new(escalationTimedOut)
                    }, "Escalate to Senior"),

                    // Feed the escalation outcome back into the terminal-status variables.
                    WithLabel(new SetVariable
                    {
                        Id = "EscalationApplyOutcome",
                        Name = "Escalation: Apply Outcome",
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var resolved = escalationResolved.Get(context);
                            if (!resolved && escalationTimedOut.Get(context))
                                timedOut.Set(context, true);
                            return resolved;
                        })
                    }, "Escalation: Apply Outcome"),

                    WithLabel(new SetVariable
                    {
                        Id = "EscalationRecordFeedback",
                        Name = "Record Escalation Feedback",
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var outcome = escalationResolved.Get(context)
                                ? "resolved by senior"
                                : escalationTimedOut.Get(context) ? "senior-response SLA expired" : "awaiting senior";
                            var newList = new List<string>(existing) { $"[Escalation] Escalated to senior developer ({outcome})" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    }, "Record Escalation Feedback")
                }
            }, "Escalation Body");

        var escalationIf = new If
        {
            Id = "EscalationCondition",
            Name = "Escalation Applicable?",
            Condition = new(context => !isResolved.Get(context)),
            Then = escalationBody
        };
        escalationIf.SetDisplayText("Escalation Applicable?");
        return escalationIf;
    }

    /// <summary>
    /// Builds a diagnosis prompt from the aggregated signals for the LLM.
    /// </summary>
    private static string BuildDiagnosisPrompt(
        AggregatedSignals? signals,
        int skillLevel,
        string? blockerContext)
    {
        var parts = new List<string>
        {
            $"Diagnose what is blocking this junior developer (skill level {skillLevel}/5).",
            ""
        };

        if (signals?.GitActivity?.CollectionSucceeded == true)
        {
            var git = signals.GitActivity;
            parts.Add($"Git Activity: {git.RecentCommitCount} recent commits, " +
                       $"{git.FilesChanged} files changed, " +
                       $"time since last commit: {git.TimeSinceLastCommit.TotalMinutes:F0} minutes");
        }

        if (signals?.CIStatus?.CollectionSucceeded == true)
        {
            var ci = signals.CIStatus;
            parts.Add($"CI Status: Build={ci.BuildStatus}, Tests={ci.PassedTests}/{ci.TotalTests} passed, " +
                       $"{ci.FailedTests} failed");
            if (!string.IsNullOrEmpty(ci.BuildError))
                parts.Add($"Build Error: {SecurityHelpers.SanitizeForPrompt(ci.BuildError)}");
            if (ci.FailingTestNames.Count > 0)
                parts.Add($"Failing Tests: {SecurityHelpers.SanitizeForPrompt(string.Join(", ", ci.FailingTestNames.Take(5)))}");
        }

        if (signals?.Inactivity?.CollectionSucceeded == true)
        {
            var inact = signals.Inactivity;
            parts.Add($"Inactivity: {inact.TimeSinceLastActivity.TotalMinutes:F0} minutes since last activity, " +
                       $"IsInactive={inact.IsInactive}");
        }

        if (signals?.Communication?.CollectionSucceeded == true)
        {
            var comms = signals.Communication;
            parts.Add($"Communication: HasRecent={comms.HasRecentCommunication}, " +
                       $"Messages={comms.RecentMessageCount}, Questions={comms.QuestionsAsked}");
        }

        if (!string.IsNullOrEmpty(blockerContext))
        {
            parts.Add("");
            parts.Add($"Additional Context: {SecurityHelpers.SanitizeForPrompt(blockerContext)}");
        }

        parts.Add("");
        parts.Add("Classify into one of: ConceptualMisunderstanding, TechnicalKnowledgeGap, EnvironmentIssue, " +
                   "DesignDecisionParalysis, DebuggingStuck, IntegrationIssue, ExternalDependency, PersonalBlocker");
        parts.Add("");
        parts.Add("Return JSON with: blocker_type, confidence (0-1), root_cause, evidence[], recommended_approach");

        return string.Join("\n", parts);
    }
}
