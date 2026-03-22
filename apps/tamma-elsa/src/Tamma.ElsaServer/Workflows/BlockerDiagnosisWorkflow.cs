using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Blocker;
using Tamma.Activities.Blocker.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;

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
/// Inputs:  sessionId, storyId, juniorId, skillLevel, blockerContext, repository, branchName
/// Outputs: BlockerResolution record
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
        // Build Workflow Tree
        // ============================================
        builder.Root = new Sequence
        {
            Activities =
            {
                // --- Step 1: Capture Inputs ---
                new SetVariable
                {
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
                },

                // --- Step 2: Parallel Signal Collection ---
                new Elsa.Workflows.Activities.Parallel
                {
                    Activities =
                    {
                        new CollectGitActivityActivity
                        {
                            Repository = new(context => repository.Get(context) ?? ""),
                            BranchName = new(context => branchName.Get(context) ?? ""),
                            Result = new(gitSignal)
                        },
                        new CollectCIStatusActivity
                        {
                            Repository = new(context => repository.Get(context) ?? ""),
                            BranchName = new(context => branchName.Get(context) ?? ""),
                            Result = new(ciSignal)
                        },
                        new CollectInactivityActivity
                        {
                            Repository = new(context => repository.Get(context) ?? ""),
                            BranchName = new(context => branchName.Get(context) ?? ""),
                            Result = new(inactivitySignal)
                        },
                        new CollectCommunicationActivity
                        {
                            JuniorId = new(context => juniorId.Get(context) ?? ""),
                            Result = new(communicationSignal)
                        }
                    }
                },

                // --- Step 3: Aggregate Signals ---
                new SetVariable
                {
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
                },

                // --- Step 4: AI Diagnosis via LLM Call ---
                new DispatchWorkflow
                {
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
                },

                // --- Step 5: Classify Blocker ---
                new ClassifyBlockerActivity
                {
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
                },

                // --- Step 6: Determine Starting Level (Skill Adaptation) ---
                new SetVariable
                {
                    Variable = currentLevel,
                    Value = new(context =>
                    {
                        var sl = skillLevel.Get(context);
                        // Level 1-2: skip Hint (Socratic too frustrating for beginners)
                        return sl <= 2 ? "Guidance" : "Hint";
                    })
                },

                // --- Step 7: Progressive Resolution ---
                // Level 1: Hint (conditional — skipped for skill 1-2)
                BuildHintLevel(sessionId, storyId, juniorId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected),

                // Level 2: Guidance (conditional — skipped if already resolved)
                BuildGuidanceLevel(sessionId, storyId, juniorId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected),

                // Level 3: Assistance (conditional — skipped if already resolved)
                BuildAssistanceLevel(sessionId, storyId, juniorId, skillLevel, diagnosisResult,
                    currentLevel, attempts, feedbackProvided, isResolved, progressDetected),

                // Level 4: Escalation (conditional — skipped if already resolved)
                BuildEscalationLevel(sessionId, storyId, juniorId, diagnosisResult,
                    aggregatedSignals, currentLevel, attempts, feedbackProvided, isResolved),

                // --- Step 8: Set Output ---
                new SetOutput
                {
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
                }
            }
        };
    }

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
        return new If
        {
            // Only execute if current level is Hint (not skipped) and not yet resolved
            Condition = new(context =>
                currentLevel.Get(context) == "Hint" && !isResolved.Get(context)),
            Then = new Sequence
            {
                Activities =
                {
                    // Dispatch LLM for Socratic hints
                    new DispatchWorkflow
                    {
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
                    },

                    // Record feedback
                    new SetVariable
                    {
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { $"[Hint] Socratic hints provided for {diagnosisResult.Get(context)?.BlockerType}" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    },

                    // Wait for progress (bookmark) — output wired to progressDetected variable
                    new DetectProgressActivity
                    {
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Hint"),
                        WaitTimeMinutes = new(context => skillLevel.Get(context) >= 4 ? 30 : 15),
                        ProgressDetected = new(progressDetected)
                    },

                    // Check if progress was detected via the progressDetected variable
                    new SetVariable
                    {
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var detected = progressDetected.Get(context);
                            if (!detected)
                                currentLevel.Set(context, "Guidance");
                            return detected;
                        })
                    }
                }
            }
        };
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
        return new If
        {
            Condition = new(context => !isResolved.Get(context)),
            Then = new Sequence
            {
                Activities =
                {
                    // Update current level
                    new SetVariable
                    {
                        Variable = currentLevel,
                        Value = new(context => "Guidance")
                    },

                    // Dispatch LLM for direct guidance
                    new DispatchWorkflow
                    {
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
                    },

                    // Record feedback
                    new SetVariable
                    {
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { $"[Guidance] Direct guidance provided for {diagnosisResult.Get(context)?.BlockerType}" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    },

                    // Wait for progress (bookmark) — output wired to progressDetected variable
                    new DetectProgressActivity
                    {
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Guidance"),
                        WaitTimeMinutes = new(30),
                        ProgressDetected = new(progressDetected)
                    },

                    // Check if progress was detected via the progressDetected variable
                    new SetVariable
                    {
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var detected = progressDetected.Get(context);
                            if (!detected)
                                currentLevel.Set(context, "Assistance");
                            return detected;
                        })
                    }
                }
            }
        };
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
        return new If
        {
            Condition = new(context => !isResolved.Get(context)),
            Then = new Sequence
            {
                Activities =
                {
                    // Update current level
                    new SetVariable
                    {
                        Variable = currentLevel,
                        Value = new(context => "Assistance")
                    },

                    // Dispatch LLM for code assistance (uses implementer role)
                    new DispatchWorkflow
                    {
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
                    },

                    // Record feedback
                    new SetVariable
                    {
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { $"[Assistance] Code example provided for {diagnosisResult.Get(context)?.BlockerType}" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    },

                    // Wait for progress (bookmark) — output wired to progressDetected variable
                    new DetectProgressActivity
                    {
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        CurrentLevel = new("Assistance"),
                        WaitTimeMinutes = new(45),
                        ProgressDetected = new(progressDetected)
                    },

                    // Check if progress was detected via the progressDetected variable
                    new SetVariable
                    {
                        Variable = isResolved,
                        Value = new(context =>
                        {
                            var detected = progressDetected.Get(context);
                            if (!detected)
                                currentLevel.Set(context, "Escalation");
                            return detected;
                        })
                    }
                }
            }
        };
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
        return new If
        {
            Condition = new(context => !isResolved.Get(context)),
            Then = new Sequence
            {
                Activities =
                {
                    // Update current level
                    new SetVariable
                    {
                        Variable = currentLevel,
                        Value = new(context => "Escalation")
                    },

                    // Escalate to senior (bookmark-based wait)
                    new EscalateToSeniorActivity
                    {
                        SessionId = new(context => sessionId.Get(context)),
                        StoryId = new(context => storyId.Get(context) ?? ""),
                        JuniorId = new(context => juniorId.Get(context) ?? ""),
                        BlockerType = new(context => diagnosisResult.Get(context)?.BlockerType.ToString() ?? "TechnicalKnowledgeGap"),
                        BlockerSeverity = new(context => diagnosisResult.Get(context)?.Severity.ToString() ?? "High"),
                        DiagnosisDetails = new(context => diagnosisResult.Get(context)?.RootCauseHypothesis ?? ""),
                        PreviousAttempts = new(context => feedbackProvided.Get(context) ?? new List<string>()),
                        Signals = new(context => aggregatedSignals.Get(context))
                    },

                    // Record escalation feedback
                    new SetVariable
                    {
                        Variable = feedbackProvided,
                        Value = new(context =>
                        {
                            var existing = feedbackProvided.Get(context) ?? new List<string>();
                            var newList = new List<string>(existing) { "[Escalation] Escalated to senior developer" };
                            attempts.Set(context, attempts.Get(context) + 1);
                            return newList;
                        })
                    }
                }
            }
        };
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
