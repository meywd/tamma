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
///   Level 4: Escalation -- wait for senior
///
/// Can be invoked standalone via ELSA REST API or as a child workflow via DispatchWorkflow.
///
/// Design: Flowchart with visible nodes for each phase in ELSA Studio.
///
/// Flow:
///   CaptureInputs → ParallelSignals → AggregateSignals → AIDiagnosis
///     → ClassifyBlocker → DetermineStartLevel → HintLevel → GuidanceLevel
///     → AssistanceLevel → EscalationLevel → SetOutput
/// </summary>
public class BlockerDiagnosisWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Blocker Diagnosis";
        builder.DefinitionId = "blocker-diagnosis";
        builder.Description = "Diagnoses blocker type and applies progressive resolution (hint -> guidance -> assistance -> escalation)";

        // ============================================
        // Workflow Variables
        // ============================================
        var sessionId = builder.WithVariable<Guid>();
        var storyId = builder.WithVariable<string>();
        var juniorId = builder.WithVariable<string>();
        var skillLevel = builder.WithVariable<int>();
        var blockerContext = builder.WithVariable<string?>();
        var repository = builder.WithVariable<string>();
        var branchName = builder.WithVariable<string>();

        // Signal variables
        var gitSignal = builder.WithVariable<GitActivitySignal>();
        var ciSignal = builder.WithVariable<CIStatusSignal>();
        var inactivitySignal = builder.WithVariable<InactivitySignal>();
        var communicationSignal = builder.WithVariable<CommunicationSignal>();
        var aggregatedSignals = builder.WithVariable<AggregatedSignals>();

        // Diagnosis variables
        var llmDiagnosisOutput = builder.WithVariable<IDictionary<string, object>?>();
        var diagnosisResult = builder.WithVariable<BlockerDiagnosisResult>();

        // Resolution tracking
        var currentLevel = builder.WithVariable<string>("Hint");
        var attempts = builder.WithVariable<int>(0);
        var feedbackProvided = builder.WithVariable<List<string>>();
        var startTime = builder.WithVariable<DateTime>();
        var isResolved = builder.WithVariable<bool>(false);
        var progressDetected = builder.WithVariable<bool>(false);

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
                ["role"] = "analyst",
                ["analysisType"] = "BlockerDiagnosis",
                ["content"] = BuildDiagnosisPrompt(
                    aggregatedSignals.Get(context),
                    skillLevel.Get(context),
                    blockerContext.Get(context)),
                ["sessionId"] = sessionId.Get(context),
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

        // 7a. Progressive Resolution — Level 1: Hint (wrapped in named Sequence)
        var hintLevel = new Sequence
        {
            Id = "HintLevel",
            Name = "Level 1: Hint",
            Activities =
            {
                BuildHintLevel(sessionId, storyId, juniorId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected)
            }
        };
        hintLevel.SetDisplayText("Level 1: Hint");

        // 7b. Progressive Resolution — Level 2: Guidance
        var guidanceLevel = new Sequence
        {
            Id = "GuidanceLevel",
            Name = "Level 2: Guidance",
            Activities =
            {
                BuildGuidanceLevel(sessionId, storyId, juniorId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected)
            }
        };
        guidanceLevel.SetDisplayText("Level 2: Guidance");

        // 7c. Progressive Resolution — Level 3: Assistance
        var assistanceLevel = new Sequence
        {
            Id = "AssistanceLevel",
            Name = "Level 3: Assistance",
            Activities =
            {
                BuildAssistanceLevel(sessionId, storyId, juniorId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected)
            }
        };
        assistanceLevel.SetDisplayText("Level 3: Assistance");

        // 7d. Progressive Resolution — Level 4: Escalation
        var escalationLevel = new Sequence
        {
            Id = "EscalationLevel",
            Name = "Level 4: Escalation",
            Activities =
            {
                BuildEscalationLevel(sessionId, storyId, juniorId, diagnosisResult,
                    aggregatedSignals, currentLevel, attempts, feedbackProvided, isResolved)
            }
        };
        escalationLevel.SetDisplayText("Level 4: Escalation");

        // 8. Set Output
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
                var wasResolved = isResolved.Get(context);

                return new BlockerResolution
                {
                    Status = wasResolved
                        ? BlockerResolutionStatus.Resolved
                        : BlockerResolutionStatus.Escalated,
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
                classifyBlocker, determineStartLevel,
                hintLevel, guidanceLevel, assistanceLevel, escalationLevel,
                setOutput
            },
            Connections =
            {
                // CaptureInputs → Collect Signals
                Connect(captureInputs, parallelSignals),

                // Collect Signals → Aggregate Signals
                Connect(parallelSignals, aggregateSignals),

                // Aggregate Signals → AI Diagnosis
                Connect(aggregateSignals, aiDiagnosis),

                // AI Diagnosis → Classify Blocker
                Connect(aiDiagnosis, classifyBlocker),

                // Classify Blocker → Determine Start Level
                Connect(classifyBlocker, determineStartLevel),

                // Determine Start Level → Hint Level
                Connect(determineStartLevel, hintLevel),

                // Hint Level → Guidance Level
                Connect(hintLevel, guidanceLevel),

                // Guidance Level → Assistance Level
                Connect(guidanceLevel, assistanceLevel),

                // Assistance Level → Escalation Level
                Connect(assistanceLevel, escalationLevel),

                // Escalation Level → Set Output
                Connect(escalationLevel, setOutput)
            }
        };
    }

    // ================================================================
    // Flowchart helpers
    // ================================================================

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

    /// <summary>
    /// Level 1: Hint (Socratic Method).
    /// Skipped for skill level 1-2. Extended timeout (30min) for skill 4-5.
    /// </summary>
    private static If BuildHintLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<int> skillLevel,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> progressDetected)
    {
        var hintBody = WithLabel(new Sequence
        {
            Id = "HintBody",
            Name = "Hint Body",
            Activities =
            {
                    // Dispatch LLM for Socratic hints
                    WithLabel(new DispatchWorkflow
                    {
                        Id = "HintLlmCall",
                        Name = "Hint LLM Call",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(context => new Dictionary<string, object>
                        {
                            ["role"] = "analyst",
                            ["analysisType"] = "GuidanceGeneration",
                            ["content"] = $"Provide Socratic hints for: {diagnosisResult.Get(context)?.RootCauseHypothesis ?? "unknown blocker"}. " +
                                          $"Blocker type: {diagnosisResult.Get(context)?.BlockerType}. " +
                                          "Use guiding questions, not direct answers. Employ the Socratic method.",
                            ["sessionId"] = sessionId.Get(context),
                            ["skillLevel"] = skillLevel.Get(context)
                        }),
                        WaitForCompletion = new(true)
                    }, "Hint LLM Call"),

                    // Record feedback
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

                    // Wait for progress (bookmark) — output wired to progressDetected variable
                    WithLabel(new DetectProgressActivity
                    {
                        Id = "HintDetectProgress",
                        Name = "Hint: Detect Progress",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Hint"),
                        WaitTimeMinutes = new(context => skillLevel.Get(context) >= 4 ? 30 : 15),
                        ProgressDetected = new(progressDetected)
                    }, "Hint: Detect Progress"),

                    // Check if progress was detected via the progressDetected variable
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
                    }, "Hint: Check Progress")
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
    /// Level 2: Direct Guidance. 30-minute wait.
    /// </summary>
    private static If BuildGuidanceLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<int> skillLevel,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> progressDetected)
    {
        var guidanceBody = WithLabel(new Sequence
        {
            Id = "GuidanceBody",
            Name = "Guidance Body",
                Activities =
                {
                    // Update current level
                    WithLabel(new SetVariable
                    {
                        Id = "SetLevelGuidance",
                        Name = "Set Level: Guidance",
                        Variable = currentLevel,
                        Value = new(context => "Guidance")
                    }, "Set Level: Guidance"),

                    // Dispatch LLM for direct guidance
                    WithLabel(new DispatchWorkflow
                    {
                        Id = "GuidanceLlmCall",
                        Name = "Guidance LLM Call",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(context => new Dictionary<string, object>
                        {
                            ["role"] = "analyst",
                            ["analysisType"] = "GuidanceGeneration",
                            ["content"] = $"Provide direct guidance for: {diagnosisResult.Get(context)?.RootCauseHypothesis ?? "unknown blocker"}. " +
                                          $"Blocker type: {diagnosisResult.Get(context)?.BlockerType}. " +
                                          "Give clear, step-by-step instructions. Be specific and actionable.",
                            ["sessionId"] = sessionId.Get(context),
                            ["skillLevel"] = skillLevel.Get(context)
                        }),
                        WaitForCompletion = new(true)
                    }, "Guidance LLM Call"),

                    // Record feedback
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

                    // Wait for progress (bookmark) — output wired to progressDetected variable
                    WithLabel(new DetectProgressActivity
                    {
                        Id = "GuidanceDetectProgress",
                        Name = "Guidance: Detect Progress",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Guidance"),
                        WaitTimeMinutes = new(30),
                        ProgressDetected = new(progressDetected)
                    }, "Guidance: Detect Progress"),

                    // Check if progress was detected via the progressDetected variable
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
                    }, "Guidance: Check Progress")
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
    /// Level 3: Code Assistance. 45-minute wait. Uses implementer role.
    /// </summary>
    private static If BuildAssistanceLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<int> skillLevel,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved,
        Variable<bool> progressDetected)
    {
        var assistanceBody = WithLabel(new Sequence
        {
            Id = "AssistanceBody",
            Name = "Assistance Body",
                Activities =
                {
                    // Update current level
                    WithLabel(new SetVariable
                    {
                        Id = "SetLevelAssistance",
                        Name = "Set Level: Assistance",
                        Variable = currentLevel,
                        Value = new(context => "Assistance")
                    }, "Set Level: Assistance"),

                    // Dispatch LLM for code assistance (uses implementer role)
                    WithLabel(new DispatchWorkflow
                    {
                        Id = "AssistanceLlmCall",
                        Name = "Assistance LLM Call",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(context => new Dictionary<string, object>
                        {
                            ["role"] = "implementer",
                            ["analysisType"] = "GuidanceGeneration",
                            ["content"] = $"Provide code example for: {diagnosisResult.Get(context)?.RootCauseHypothesis ?? "unknown blocker"}. " +
                                          $"Blocker type: {diagnosisResult.Get(context)?.BlockerType}. " +
                                          "Include a working code example with detailed explanation. " +
                                          "Show the solution step by step.",
                            ["sessionId"] = sessionId.Get(context),
                            ["skillLevel"] = skillLevel.Get(context)
                        }),
                        WaitForCompletion = new(true)
                    }, "Assistance LLM Call"),

                    // Record feedback
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

                    // Wait for progress (bookmark) — output wired to progressDetected variable
                    WithLabel(new DetectProgressActivity
                    {
                        Id = "AssistanceDetectProgress",
                        Name = "Assistance: Detect Progress",
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Assistance"),
                        WaitTimeMinutes = new(45),
                        ProgressDetected = new(progressDetected)
                    }, "Assistance: Detect Progress"),

                    // Check if progress was detected via the progressDetected variable
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
                    }, "Assistance: Check Progress")
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
    /// Level 4: Senior Escalation. Compiles context dump, notifies senior, waits via bookmark.
    /// </summary>
    private static If BuildEscalationLevel(
        Variable<Guid> sessionId,
        Variable<string> storyId,
        Variable<string> juniorId,
        Variable<BlockerDiagnosisResult> diagnosisResult,
        Variable<AggregatedSignals> aggregatedSignals,
        Variable<string> currentLevel,
        Variable<int> attempts,
        Variable<List<string>> feedbackProvided,
        Variable<bool> isResolved)
    {
        var escalationBody = WithLabel(new Sequence
        {
            Id = "EscalationBody",
            Name = "Escalation Body",
                Activities =
                {
                    // Update current level
                    WithLabel(new SetVariable
                    {
                        Id = "SetLevelEscalation",
                        Name = "Set Level: Escalation",
                        Variable = currentLevel,
                        Value = new(context => "Escalation")
                    }, "Set Level: Escalation"),

                    // Escalate to senior (bookmark-based wait)
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
                        Signals = new(context => aggregatedSignals.Get(context))
                    }, "Escalate to Senior"),

                    // Record escalation feedback
                    WithLabel(new SetVariable
                    {
                        Id = "EscalationRecordFeedback",
                        Name = "Record Escalation Feedback",
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { "[Escalation] Escalated to senior developer" };
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
                parts.Add($"Build Error: {ci.BuildError}");
            if (ci.FailingTestNames.Count > 0)
                parts.Add($"Failing Tests: {string.Join(", ", ci.FailingTestNames.Take(5))}");
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
            parts.Add($"Additional Context: {blockerContext}");
        }

        parts.Add("");
        parts.Add("Classify into one of: ConceptualMisunderstanding, TechnicalKnowledgeGap, EnvironmentIssue, " +
                   "DesignDecisionParalysis, DebuggingStuck, IntegrationIssue, ExternalDependency, PersonalBlocker");
        parts.Add("");
        parts.Add("Return JSON with: blocker_type, confidence (0-1), root_cause, evidence[], recommended_approach");

        return string.Join("\n", parts);
    }
}
