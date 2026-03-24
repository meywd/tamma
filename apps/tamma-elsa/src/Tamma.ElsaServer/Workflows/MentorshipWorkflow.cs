using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Mentorship;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Main Mentorship Workflow (Story 7-1A) — Code-First Flowchart
///
/// This is the top-level ELSA workflow that orchestrates the entire mentorship
/// session lifecycle across all 28 MentorshipState values. It uses a Flowchart
/// root activity with outcome-based routing between FlowNode activities.
///
/// Key paths:
///   Happy path:   INIT -> VALIDATE -> ASSESS -> PLAN -> IMPLEMENT -> MONITOR ->
///                 QUALITY -> REVIEW -> MERGE -> REPORT -> PROFILE -> COMPLETED
///   Bug fast path: INIT -> VALIDATE -> [BugIssue] -> Debugging sub-workflow -> QUALITY
///   Assessment loop: ASSESS -> CLARIFY -> ASSESS (max 3)
///   Planning loop:   PLAN -> REVIEW -> ADJUST -> PLAN (max 2)
///   Blocker escalation: DIAGNOSE -> HINT -> GUIDANCE -> ASSISTANCE -> ESCALATE
///   Quality retry: QUALITY -> AUTO_FIX -> QUALITY (max 3)
///   Review iteration: REVIEW -> GUIDE -> RE_REQUEST -> REVIEW (max 5)
///
/// Sub-workflow invocations (DispatchWorkflow):
///   - LlmCallWorkflow (7-1B)
///   - ContextGatheringWorkflow (7-1F)
///   - TestingWorkflow (7-1C)
///   - CodeReviewWorkflow (7-1D)
///   - AssessmentWorkflow (7-1E)
///   - BlockerDiagnosisWorkflow (7-1G)
///   - TddWorkflow (7-1H)
///   - DebuggingWorkflow (7-1I)
/// </summary>
public class MentorshipWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Main Mentorship Workflow";
        builder.DefinitionId = "mentorship";
        builder.Description = "Orchestrates the complete mentorship session lifecycle with 28 states, " +
                              "outcome-based routing, guard conditions, and sub-workflow invocations.";
        builder.Version = 1;

        // =====================================================================
        // Workflow Variables
        // =====================================================================
        var sessionId = builder.WithVariable<Guid>("SessionId", default(Guid));
        var storyId = builder.WithVariable<string>("StoryId", "");
        var juniorId = builder.WithVariable<string>("JuniorId", "");
        var assessmentAttempt = builder.WithVariable<int>("AssessmentAttempt", 0);
        var planIteration = builder.WithVariable<int>("PlanIteration", 0);
        var qualityRetryCount = builder.WithVariable<int>("QualityRetryCount", 0);
        var reviewIteration = builder.WithVariable<int>("ReviewIteration", 0);
        var blockerEscalationLevel = builder.WithVariable<int>("BlockerEscalationLevel", 0);

        // =====================================================================
        // 1. INITIALIZATION ACTIVITIES
        // =====================================================================

        var initStoryProcessing = new InitStoryProcessingActivity
        {
            Id = "InitStoryProcessing",
            Name = "INIT_STORY_PROCESSING"
        };

        var validateStory = new ValidateStoryActivity
        {
            Id = "ValidateStory",
            Name = "VALIDATE_STORY"
        };

        // =====================================================================
        // 2. ASSESSMENT ACTIVITIES
        // =====================================================================

        var assessJunior = new AssessJuniorFlowActivity
        {
            Id = "AssessJuniorCapability",
            Name = "ASSESS_JUNIOR_CAPABILITY"
        };

        var clarifyRequirements = new ClarifyRequirementsActivity
        {
            Id = "ClarifyRequirements",
            Name = "CLARIFY_REQUIREMENTS"
        };

        var reExplainStory = new ReExplainStoryActivity
        {
            Id = "ReExplainStory",
            Name = "RE_EXPLAIN_STORY"
        };

        // =====================================================================
        // 3. PLANNING ACTIVITIES
        // =====================================================================

        var planDecomposition = new PlanDecompositionActivity
        {
            Id = "PlanDecomposition",
            Name = "PLAN_DECOMPOSITION"
        };

        var reviewPlan = new ReviewPlanActivity
        {
            Id = "ReviewPlan",
            Name = "REVIEW_PLAN"
        };

        var adjustPlan = new AdjustPlanActivity
        {
            Id = "AdjustPlan",
            Name = "ADJUST_PLAN"
        };

        // =====================================================================
        // 4. IMPLEMENTATION ACTIVITIES
        // =====================================================================

        var startImplementation = new StartImplementationActivity
        {
            Id = "StartImplementation",
            Name = "START_IMPLEMENTATION"
        };

        var monitorProgress = new MonitorProgressFlowActivity
        {
            Id = "MonitorProgress",
            Name = "MONITOR_PROGRESS"
        };

        var detectPattern = new DetectPatternActivity
        {
            Id = "DetectPattern",
            Name = "DETECT_PATTERN"
        };

        // =====================================================================
        // 5. BLOCKER ACTIVITIES
        // =====================================================================

        var diagnoseBlocker = new DiagnoseBlockerFlowActivity
        {
            Id = "DiagnoseBlocker",
            Name = "DIAGNOSE_BLOCKER"
        };

        var provideHint = new ProvideHintFlowActivity
        {
            Id = "ProvideHint",
            Name = "PROVIDE_HINT"
        };

        var provideGuidance = new ProvideGuidanceFlowActivity
        {
            Id = "ProvideGuidance",
            Name = "PROVIDE_GUIDANCE"
        };

        var provideAssistance = new ProvideAssistanceFlowActivity
        {
            Id = "ProvideAssistance",
            Name = "PROVIDE_ASSISTANCE"
        };

        var escalateToSenior = new EscalateToSeniorActivity
        {
            Id = "EscalateToSenior",
            Name = "ESCALATE_TO_SENIOR"
        };

        // =====================================================================
        // 6. QUALITY ACTIVITIES
        // =====================================================================

        var qualityGateCheck = new QualityGateFlowActivity
        {
            Id = "QualityGateCheck",
            Name = "QUALITY_GATE_CHECK"
        };

        var autoFixIssues = new AutoFixIssuesActivity
        {
            Id = "AutoFixIssues",
            Name = "AUTO_FIX_ISSUES"
        };

        var manualFixRequired = new ManualFixRequiredActivity
        {
            Id = "ManualFixRequired",
            Name = "MANUAL_FIX_REQUIRED"
        };

        // =====================================================================
        // 7. REVIEW ACTIVITIES
        // =====================================================================

        var prepareCodeReview = new PrepareCodeReviewFlowActivity
        {
            Id = "PrepareCodeReview",
            Name = "PREPARE_CODE_REVIEW"
        };

        var monitorReview = new MonitorReviewFlowActivity
        {
            Id = "MonitorReview",
            Name = "MONITOR_REVIEW"
        };

        var guideFixes = new GuideFixesFlowActivity
        {
            Id = "GuideFixes",
            Name = "GUIDE_FIXES"
        };

        var reRequestReview = new ReRequestReviewActivity
        {
            Id = "ReRequestReview",
            Name = "RE_REQUEST_REVIEW"
        };

        // =====================================================================
        // 8. COMPLETION ACTIVITIES
        // =====================================================================

        var mergeAndComplete = new MergeAndCompleteFlowActivity
        {
            Id = "MergeAndComplete",
            Name = "MERGE_AND_COMPLETE"
        };

        var generateReport = new GenerateReportFlowActivity
        {
            Id = "GenerateReport",
            Name = "GENERATE_REPORT"
        };

        var updateSkillProfile = new UpdateSkillProfileFlowActivity
        {
            Id = "UpdateSkillProfile",
            Name = "UPDATE_SKILL_PROFILE"
        };

        var completed = new CompletedActivity
        {
            Id = "Completed",
            Name = "COMPLETED"
        };

        // =====================================================================
        // 9. EXCEPTION STATE ACTIVITIES
        // =====================================================================

        var paused = new PauseSessionActivity
        {
            Id = "Paused",
            Name = "PAUSED"
        };

        var cancelled = new CancelSessionActivity
        {
            Id = "Cancelled",
            Name = "CANCELLED"
        };

        var failed = new FailSessionActivity
        {
            Id = "Failed",
            Name = "FAILED"
        };

        var timeout = new TimeoutSessionActivity
        {
            Id = "Timeout",
            Name = "TIMEOUT"
        };

        // =====================================================================
        // 10. SUB-WORKFLOW INVOCATIONS (DispatchWorkflow)
        // =====================================================================

        // 7-1B: LLM Call Workflow — used during assessment and planning
        var llmCallWorkflow = new DispatchWorkflow
        {
            Id = "DispatchLlmCall",
            Name = "Dispatch LLM Call (7-1B)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(context => new Dictionary<string, object>
            {
                ["agentRole"] = "mentor",
                ["taskPrompt"] = "Generate plan decomposition",
                ["sessionId"] = sessionId.Get(context).ToString()
            }),
            WaitForCompletion = new(true)
        };

        // 7-1F: Context Gathering Workflow — used during init
        var contextGatheringWorkflow = new DispatchWorkflow
        {
            Id = "DispatchContextGathering",
            Name = "Dispatch Context Gathering (7-1F)",
            WorkflowDefinitionId = new("context-gathering"),
            Input = new(context => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(context),
                ["StoryId"] = storyId.Get(context) ?? "",
                ["Purpose"] = "Assessment",
                ["MaxContextSize"] = 50000
            }),
            WaitForCompletion = new(true)
        };

        // 7-1C: Testing Workflow — used during quality gate
        var testingWorkflow = new DispatchWorkflow
        {
            Id = "DispatchTesting",
            Name = "Dispatch Testing (7-1C)",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(context => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(context),
                ["SkillLevel"] = 3
            }),
            WaitForCompletion = new(true)
        };

        // 7-1D: Code Review Workflow — used during review phase
        var codeReviewWorkflow = new DispatchWorkflow
        {
            Id = "DispatchCodeReview",
            Name = "Dispatch Code Review (7-1D)",
            WorkflowDefinitionId = new("code-review"),
            Input = new(context => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(context).ToString(),
                ["StoryId"] = storyId.Get(context) ?? "",
                ["JuniorId"] = juniorId.Get(context) ?? ""
            }),
            WaitForCompletion = new(true)
        };

        // 7-1E: Assessment Workflow — used during assessment phase
        var assessmentWorkflow = new DispatchWorkflow
        {
            Id = "DispatchAssessment",
            Name = "Dispatch Assessment (7-1E)",
            WorkflowDefinitionId = new("assessment"),
            Input = new(context => new Dictionary<string, object>
            {
                ["sessionId"] = sessionId.Get(context),
                ["storyId"] = storyId.Get(context) ?? "",
                ["juniorId"] = juniorId.Get(context) ?? "",
                ["skillLevel"] = 3
            }),
            WaitForCompletion = new(true)
        };

        // 7-1G: Blocker Diagnosis Workflow — used during blocker diagnosis
        var blockerDiagnosisWorkflow = new DispatchWorkflow
        {
            Id = "DispatchBlockerDiagnosis",
            Name = "Dispatch Blocker Diagnosis (7-1G)",
            WorkflowDefinitionId = new("blocker-diagnosis"),
            Input = new(context => new Dictionary<string, object>
            {
                ["sessionId"] = sessionId.Get(context),
                ["storyId"] = storyId.Get(context) ?? "",
                ["juniorId"] = juniorId.Get(context) ?? "",
                ["skillLevel"] = 3,
                ["repository"] = "",
                ["branchName"] = ""
            }),
            WaitForCompletion = new(true)
        };

        // 7-1H: TDD Workflow — used during implementation (invoked from START_IMPLEMENTATION)
        var tddWorkflow = new DispatchWorkflow
        {
            Id = "DispatchTdd",
            Name = "Dispatch TDD (7-1H)",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(context => new Dictionary<string, object>
            {
                ["sessionId"] = sessionId.Get(context),
                ["storyId"] = storyId.Get(context) ?? "",
                ["taskDescription"] = "",
                ["skillLevel"] = 3
            }),
            WaitForCompletion = new(true)
        };

        // 7-1I: Debugging Workflow — used for bug fast path
        var debuggingWorkflow = new DispatchWorkflow
        {
            Id = "DispatchDebugging",
            Name = "Dispatch Debugging (7-1I)",
            WorkflowDefinitionId = new("debugging"),
            Input = new(context => new Dictionary<string, object>
            {
                ["sessionId"] = sessionId.Get(context),
                ["storyId"] = storyId.Get(context) ?? "",
                ["debugContextMode"] = "BugInvestigation",
                ["skillLevel"] = 3
            }),
            WaitForCompletion = new(true)
        };

        // =====================================================================
        // 11. GUARD CONDITION ACTIVITIES (FlowDecision)
        // =====================================================================

        // Guard: Max assessment retries not exceeded (max 3)
        var guardAssessmentRetries = new FlowDecision(
            context => assessmentAttempt.Get(context) < 3)
        {
            Id = "GuardAssessmentRetries",
            Name = "Assessment Retries < 3?"
        };

        // Guard: Max plan iterations not exceeded (max 2)
        var guardPlanIterations = new FlowDecision(
            context => planIteration.Get(context) < 2)
        {
            Id = "GuardPlanIterations",
            Name = "Plan Iterations < 2?"
        };

        // Guard: Max quality retries not exceeded (max 3)
        var guardQualityRetries = new FlowDecision(
            context => qualityRetryCount.Get(context) < 3)
        {
            Id = "GuardQualityRetries",
            Name = "Quality Retries < 3?"
        };

        // Guard: Max review iterations not exceeded (max 5)
        var guardReviewIterations = new FlowDecision(
            context => reviewIteration.Get(context) < 5)
        {
            Id = "GuardReviewIterations",
            Name = "Review Iterations < 5?"
        };

        // Guard: Blocker escalation level check
        var guardBlockerEscalation = new FlowDecision(
            context => blockerEscalationLevel.Get(context) < 4)
        {
            Id = "GuardBlockerEscalation",
            Name = "Blocker Escalation < 4?"
        };

        // =====================================================================
        // 12. COUNTER INCREMENT ACTIVITIES (SetVariable<int>)
        // =====================================================================

        var incrementAssessmentAttempt = new SetVariable<int>(
            assessmentAttempt,
            context => assessmentAttempt.Get(context) + 1)
        {
            Id = "IncrAssessmentAttempt",
            Name = "Increment Assessment Attempt"
        };

        var incrementPlanIteration = new SetVariable<int>(
            planIteration,
            context => planIteration.Get(context) + 1)
        {
            Id = "IncrPlanIteration",
            Name = "Increment Plan Iteration"
        };

        var incrementQualityRetry = new SetVariable<int>(
            qualityRetryCount,
            context => qualityRetryCount.Get(context) + 1)
        {
            Id = "IncrQualityRetry",
            Name = "Increment Quality Retry"
        };

        var incrementReviewIteration = new SetVariable<int>(
            reviewIteration,
            context => reviewIteration.Get(context) + 1)
        {
            Id = "IncrReviewIteration",
            Name = "Increment Review Iteration"
        };

        var incrementBlockerLevel = new SetVariable<int>(
            blockerEscalationLevel,
            context => blockerEscalationLevel.Get(context) + 1)
        {
            Id = "IncrBlockerLevel",
            Name = "Increment Blocker Level"
        };

        var resetQualityRetry = new SetVariable<int>(
            qualityRetryCount, 0)
        {
            Id = "ResetQualityRetry",
            Name = "Reset Quality Retry Counter"
        };

        var resetReviewIteration = new SetVariable<int>(
            reviewIteration, 0)
        {
            Id = "ResetReviewIteration",
            Name = "Reset Review Iteration Counter"
        };

        var resetBlockerLevel = new SetVariable<int>(
            blockerEscalationLevel, 0)
        {
            Id = "ResetBlockerLevel",
            Name = "Reset Blocker Level"
        };

        // Second reset for guidance Done path
        var resetBlockerLevelForGuidance = new SetVariable<int>(
            blockerEscalationLevel, 0)
        {
            Id = "ResetBlockerLevelGuidance",
            Name = "Reset Blocker Level (Guidance)"
        };

        // =====================================================================
        // FLOWCHART DEFINITION
        // =====================================================================

        builder.Root = new Flowchart
        {
            Id = "MentorshipFlowchart",
            Name = "Mentorship Flowchart",

            // Start activity is the first in the flow
            Start = initStoryProcessing,

            // ================================================================
            // ALL ACTIVITIES (28 state activities + guards + sub-workflows +
            // counter increments)
            // ================================================================
            Activities =
            {
                // Initialization (2)
                initStoryProcessing,
                validateStory,

                // Assessment (3)
                assessJunior,
                clarifyRequirements,
                reExplainStory,

                // Planning (3)
                planDecomposition,
                reviewPlan,
                adjustPlan,

                // Implementation (3)
                startImplementation,
                monitorProgress,
                detectPattern,

                // Blocker (4)
                diagnoseBlocker,
                provideHint,
                provideGuidance,
                provideAssistance,
                escalateToSenior,

                // Quality (3)
                qualityGateCheck,
                autoFixIssues,
                manualFixRequired,

                // Review (4)
                prepareCodeReview,
                monitorReview,
                guideFixes,
                reRequestReview,

                // Completion (4)
                mergeAndComplete,
                generateReport,
                updateSkillProfile,
                completed,

                // Exception States (4)
                paused,
                cancelled,
                failed,
                timeout,

                // Sub-Workflow Dispatches (8)
                llmCallWorkflow,
                contextGatheringWorkflow,
                testingWorkflow,
                codeReviewWorkflow,
                assessmentWorkflow,
                blockerDiagnosisWorkflow,
                tddWorkflow,
                debuggingWorkflow,

                // Guard Conditions (5)
                guardAssessmentRetries,
                guardPlanIterations,
                guardQualityRetries,
                guardReviewIterations,
                guardBlockerEscalation,

                // Counter Increments (5) + Resets (3)
                incrementAssessmentAttempt,
                incrementPlanIteration,
                incrementQualityRetry,
                incrementReviewIteration,
                incrementBlockerLevel,
                resetQualityRetry,
                resetReviewIteration,
                resetBlockerLevel,
                resetBlockerLevelForGuidance,
            },

            // ================================================================
            // ALL CONNECTIONS (60+ outcome-based transitions)
            // ================================================================
            Connections =
            {
                // =============================================================
                // INITIALIZATION PATH
                // INIT -> ContextGathering -> VALIDATE
                // =============================================================

                // 1. INIT_STORY_PROCESSING -> Context Gathering sub-workflow
                new(new FlowEndpoint(initStoryProcessing, "Done"),
                    new FlowEndpoint(contextGatheringWorkflow)),

                // 2. INIT_STORY_PROCESSING Error -> FAILED
                new(new FlowEndpoint(initStoryProcessing, "Error"),
                    new FlowEndpoint(failed)),

                // 3. Context Gathering -> VALIDATE_STORY
                new(contextGatheringWorkflow, validateStory),

                // =============================================================
                // VALIDATION PATH
                // VALIDATE -> [Valid] -> Assessment sub-workflow -> ASSESS
                // VALIDATE -> [BugIssue] -> Debugging sub-workflow -> QUALITY
                // VALIDATE -> [Invalid] -> FAILED
                // =============================================================

                // 4. VALIDATE Valid -> ASSESS_JUNIOR_CAPABILITY directly
                new(new FlowEndpoint(validateStory, "Valid"),
                    new FlowEndpoint(assessJunior)),

                // 5. Assessment sub-workflow -> ASSESS (invoked when needed from ASSESS phase)
                new(assessmentWorkflow, assessJunior),

                // 6. VALIDATE -> Debugging sub-workflow (Bug fast path)
                new(new FlowEndpoint(validateStory, "BugIssue"),
                    new FlowEndpoint(debuggingWorkflow)),

                // 7. Debugging sub-workflow -> QUALITY_GATE_CHECK (bug fast path exit)
                new(debuggingWorkflow, qualityGateCheck),

                // 8. VALIDATE Invalid -> FAILED
                new(new FlowEndpoint(validateStory, "Invalid"),
                    new FlowEndpoint(failed)),

                // 9. VALIDATE Error -> FAILED
                new(new FlowEndpoint(validateStory, "Error"),
                    new FlowEndpoint(failed)),

                // =============================================================
                // ASSESSMENT PATH (loop max 3)
                // ASSESS -> [Correct] -> PLAN
                // ASSESS -> [Partial] -> IncrAttempt -> GuardRetries -> CLARIFY -> ASSESS
                // ASSESS -> [Incorrect] -> IncrAttempt -> GuardRetries -> RE_EXPLAIN -> ASSESS
                // =============================================================

                // 10. ASSESS Correct -> LLM Call (for plan generation) -> PLAN_DECOMPOSITION
                new(new FlowEndpoint(assessJunior, "Correct"),
                    new FlowEndpoint(llmCallWorkflow)),

                // 11. LLM Call -> PLAN_DECOMPOSITION
                new(llmCallWorkflow, planDecomposition),

                // 12. ASSESS Partial -> Increment Assessment Attempt
                new(new FlowEndpoint(assessJunior, "Partial"),
                    new FlowEndpoint(incrementAssessmentAttempt)),

                // 13. Increment -> Guard Assessment Retries
                new(incrementAssessmentAttempt, guardAssessmentRetries),

                // 14. Guard True (retries remaining) -> CLARIFY_REQUIREMENTS
                new(new FlowEndpoint(guardAssessmentRetries, "True"),
                    new FlowEndpoint(clarifyRequirements)),

                // 15. Guard False (max retries) -> ESCALATE_TO_SENIOR (prevents infinite loop)
                new(new FlowEndpoint(guardAssessmentRetries, "False"),
                    new FlowEndpoint(escalateToSenior)),

                // 16. ASSESS Incorrect -> RE_EXPLAIN_STORY directly
                new(new FlowEndpoint(assessJunior, "Incorrect"),
                    new FlowEndpoint(reExplainStory)),

                // 17. ASSESS Error -> FAILED
                new(new FlowEndpoint(assessJunior, "Error"),
                    new FlowEndpoint(failed)),

                // 17b. ASSESS Timeout -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(assessJunior, "Timeout"),
                    new FlowEndpoint(diagnoseBlocker)),

                // 18. CLARIFY Clarified -> ASSESS (re-assess after clarification)
                new(new FlowEndpoint(clarifyRequirements, "Clarified"),
                    new FlowEndpoint(assessJunior)),

                // 19. CLARIFY MaxRetries -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(clarifyRequirements, "MaxRetries"),
                    new FlowEndpoint(escalateToSenior)),

                // 20. CLARIFY Error -> FAILED
                new(new FlowEndpoint(clarifyRequirements, "Error"),
                    new FlowEndpoint(failed)),

                // 21. RE_EXPLAIN Explained -> ASSESS (re-assess after explanation)
                new(new FlowEndpoint(reExplainStory, "Explained"),
                    new FlowEndpoint(assessJunior)),

                // 22. RE_EXPLAIN MaxRetries -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(reExplainStory, "MaxRetries"),
                    new FlowEndpoint(escalateToSenior)),

                // 23. RE_EXPLAIN Error -> FAILED
                new(new FlowEndpoint(reExplainStory, "Error"),
                    new FlowEndpoint(failed)),

                // =============================================================
                // PLANNING PATH (loop max 2)
                // PLAN -> REVIEW -> [Approved] -> START_IMPLEMENTATION
                // PLAN -> REVIEW -> [NeedsAdjustment] -> ADJUST -> PLAN
                // =============================================================

                // 24. PLAN_DECOMPOSITION -> REVIEW_PLAN
                new(new FlowEndpoint(planDecomposition, "Planned"),
                    new FlowEndpoint(reviewPlan)),

                // 25. PLAN Error -> FAILED
                new(new FlowEndpoint(planDecomposition, "Error"),
                    new FlowEndpoint(failed)),

                // 26. REVIEW Approved -> START_IMPLEMENTATION directly
                new(new FlowEndpoint(reviewPlan, "Approved"),
                    new FlowEndpoint(startImplementation)),

                // 28. REVIEW NeedsAdjustment -> Increment Plan Iteration
                new(new FlowEndpoint(reviewPlan, "NeedsAdjustment"),
                    new FlowEndpoint(incrementPlanIteration)),

                // 29. Increment -> Guard Plan Iterations
                new(incrementPlanIteration, guardPlanIterations),

                // 30. Guard True (iterations remaining) -> ADJUST_PLAN
                new(new FlowEndpoint(guardPlanIterations, "True"),
                    new FlowEndpoint(adjustPlan)),

                // 31. Guard False (max iterations) -> START_IMPLEMENTATION anyway
                new(new FlowEndpoint(guardPlanIterations, "False"),
                    new FlowEndpoint(startImplementation)),

                // 32. REVIEW MaxRetries -> START_IMPLEMENTATION (proceed with best plan)
                new(new FlowEndpoint(reviewPlan, "MaxRetries"),
                    new FlowEndpoint(startImplementation)),

                // 33. REVIEW Error -> FAILED
                new(new FlowEndpoint(reviewPlan, "Error"),
                    new FlowEndpoint(failed)),

                // 34. ADJUST Adjusted -> PLAN_DECOMPOSITION (re-plan)
                new(new FlowEndpoint(adjustPlan, "Adjusted"),
                    new FlowEndpoint(planDecomposition)),

                // 35. ADJUST Error -> FAILED
                new(new FlowEndpoint(adjustPlan, "Error"),
                    new FlowEndpoint(failed)),

                // =============================================================
                // IMPLEMENTATION PATH
                // START -> MONITOR -> [Steady] -> MONITOR (loop)
                // START -> MONITOR -> [Complete] -> QUALITY
                // START -> MONITOR -> [Stalled] -> DIAGNOSE
                // START -> MONITOR -> [Circular] -> DETECT_PATTERN
                // START -> MONITOR -> [Slowing] -> PROVIDE_GUIDANCE
                // =============================================================

                // 36. START_IMPLEMENTATION -> TDD sub-workflow -> MONITOR_PROGRESS
                new(new FlowEndpoint(startImplementation, "Started"),
                    new FlowEndpoint(tddWorkflow)),

                // 36b. TDD sub-workflow -> MONITOR_PROGRESS
                new(tddWorkflow, monitorProgress),

                // 37. START Error -> FAILED
                new(new FlowEndpoint(startImplementation, "Error"),
                    new FlowEndpoint(failed)),

                // 38. MONITOR Steady -> MONITOR (continue monitoring loop)
                new(new FlowEndpoint(monitorProgress, "Steady"),
                    new FlowEndpoint(monitorProgress)),

                // 39. MONITOR Complete -> Reset Quality Retry -> QUALITY_GATE_CHECK
                new(new FlowEndpoint(monitorProgress, "Complete"),
                    new FlowEndpoint(resetQualityRetry)),

                // 40. Reset Quality Retry -> QUALITY_GATE_CHECK
                new(resetQualityRetry, qualityGateCheck),

                // 41. MONITOR Stalled -> Blocker Diagnosis sub-workflow -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(monitorProgress, "Stalled"),
                    new FlowEndpoint(blockerDiagnosisWorkflow)),

                // 42. Blocker Diagnosis sub-workflow -> DIAGNOSE_BLOCKER
                new(blockerDiagnosisWorkflow, diagnoseBlocker),

                // 43. MONITOR Circular -> DETECT_PATTERN
                new(new FlowEndpoint(monitorProgress, "Circular"),
                    new FlowEndpoint(detectPattern)),

                // 44. MONITOR Slowing -> PROVIDE_GUIDANCE
                new(new FlowEndpoint(monitorProgress, "Slowing"),
                    new FlowEndpoint(provideGuidance)),

                // 45. MONITOR Error -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(monitorProgress, "Error"),
                    new FlowEndpoint(diagnoseBlocker)),

                // 46. DETECT_PATTERN PatternFound -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(detectPattern, "PatternFound"),
                    new FlowEndpoint(diagnoseBlocker)),

                // 47. DETECT_PATTERN NoPattern -> MONITOR (continue)
                new(new FlowEndpoint(detectPattern, "NoPattern"),
                    new FlowEndpoint(monitorProgress)),

                // 48. DETECT_PATTERN Error -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(detectPattern, "Error"),
                    new FlowEndpoint(diagnoseBlocker)),

                // =============================================================
                // BLOCKER ESCALATION PATH
                // DIAGNOSE -> [Hint] -> PROVIDE_HINT -> MONITOR
                // DIAGNOSE -> [Guidance] -> PROVIDE_GUIDANCE -> MONITOR
                // DIAGNOSE -> [Assistance] -> PROVIDE_ASSISTANCE -> IMPLEMENTATION
                // DIAGNOSE -> [Escalate] -> ESCALATE_TO_SENIOR
                // Escalation ladder: HINT -> GUIDANCE -> ASSISTANCE -> ESCALATE
                // =============================================================

                // 49. DIAGNOSE Hint -> Increment Blocker Level -> PROVIDE_HINT
                new(new FlowEndpoint(diagnoseBlocker, "Hint"),
                    new FlowEndpoint(incrementBlockerLevel)),

                // 50. Increment Blocker Level -> Guard Blocker Escalation
                new(incrementBlockerLevel, guardBlockerEscalation),

                // 51. Guard True (can still help) -> PROVIDE_HINT
                new(new FlowEndpoint(guardBlockerEscalation, "True"),
                    new FlowEndpoint(provideHint)),

                // 52. Guard False (escalation needed) -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(guardBlockerEscalation, "False"),
                    new FlowEndpoint(escalateToSenior)),

                // 53. DIAGNOSE Guidance -> PROVIDE_GUIDANCE
                new(new FlowEndpoint(diagnoseBlocker, "Guidance"),
                    new FlowEndpoint(provideGuidance)),

                // 54. DIAGNOSE Assistance -> PROVIDE_ASSISTANCE
                new(new FlowEndpoint(diagnoseBlocker, "Assistance"),
                    new FlowEndpoint(provideAssistance)),

                // 55. DIAGNOSE Escalate -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(diagnoseBlocker, "Escalate"),
                    new FlowEndpoint(escalateToSenior)),

                // 56. DIAGNOSE Error -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(diagnoseBlocker, "Error"),
                    new FlowEndpoint(escalateToSenior)),

                // 57. PROVIDE_HINT Done -> Reset Blocker Level -> MONITOR_PROGRESS
                new(new FlowEndpoint(provideHint, "Done"),
                    new FlowEndpoint(resetBlockerLevel)),

                // 58. Reset Blocker Level -> MONITOR_PROGRESS
                new(resetBlockerLevel, monitorProgress),

                // 59. PROVIDE_HINT Error -> PROVIDE_GUIDANCE (escalate to next level)
                new(new FlowEndpoint(provideHint, "Error"),
                    new FlowEndpoint(provideGuidance)),

                // 60. PROVIDE_GUIDANCE Done -> Reset Blocker Level -> MONITOR_PROGRESS
                new(new FlowEndpoint(provideGuidance, "Done"),
                    new FlowEndpoint(resetBlockerLevelForGuidance)),

                // 60b. Reset Blocker Level (Guidance) -> MONITOR_PROGRESS
                new(resetBlockerLevelForGuidance, monitorProgress),

                // 61. PROVIDE_GUIDANCE Error -> PROVIDE_ASSISTANCE (escalate)
                new(new FlowEndpoint(provideGuidance, "Error"),
                    new FlowEndpoint(provideAssistance)),

                // 62. PROVIDE_ASSISTANCE Done -> START_IMPLEMENTATION (restart impl)
                new(new FlowEndpoint(provideAssistance, "Done"),
                    new FlowEndpoint(startImplementation)),

                // 63. PROVIDE_ASSISTANCE Error -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(provideAssistance, "Error"),
                    new FlowEndpoint(escalateToSenior)),

                // 64. ESCALATE Escalated -> PAUSED (wait for senior intervention)
                new(new FlowEndpoint(escalateToSenior, "Escalated"),
                    new FlowEndpoint(paused)),

                // 65. ESCALATE Error -> FAILED
                new(new FlowEndpoint(escalateToSenior, "Error"),
                    new FlowEndpoint(failed)),

                // =============================================================
                // QUALITY PATH (retry max 3)
                // QUALITY -> [Passed] -> PREPARE_CODE_REVIEW
                // QUALITY -> [Failed] -> Guard -> AUTO_FIX -> QUALITY
                // AUTO_FIX -> [ManualFixNeeded] -> MANUAL_FIX -> QUALITY
                // =============================================================

                // 66. QUALITY Passed -> Testing sub-workflow -> PREPARE_CODE_REVIEW
                new(new FlowEndpoint(qualityGateCheck, "Passed"),
                    new FlowEndpoint(testingWorkflow)),

                // 67. Testing sub-workflow -> Reset Review Iteration -> PREPARE_CODE_REVIEW
                new(testingWorkflow, resetReviewIteration),

                // 68. Reset Review Iteration -> PREPARE_CODE_REVIEW
                new(resetReviewIteration, prepareCodeReview),

                // 69. QUALITY Failed -> Increment Quality Retry
                new(new FlowEndpoint(qualityGateCheck, "Failed"),
                    new FlowEndpoint(incrementQualityRetry)),

                // 70. Increment -> Guard Quality Retries
                new(incrementQualityRetry, guardQualityRetries),

                // 71. Guard True (retries remaining) -> AUTO_FIX_ISSUES
                new(new FlowEndpoint(guardQualityRetries, "True"),
                    new FlowEndpoint(autoFixIssues)),

                // 72. Guard False (max retries) -> MANUAL_FIX_REQUIRED
                new(new FlowEndpoint(guardQualityRetries, "False"),
                    new FlowEndpoint(manualFixRequired)),

                // 73. QUALITY Error -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(qualityGateCheck, "Error"),
                    new FlowEndpoint(diagnoseBlocker)),

                // 74. AUTO_FIX Fixed -> QUALITY_GATE_CHECK (retry)
                new(new FlowEndpoint(autoFixIssues, "Fixed"),
                    new FlowEndpoint(qualityGateCheck)),

                // 75. AUTO_FIX ManualFixNeeded -> MANUAL_FIX_REQUIRED
                new(new FlowEndpoint(autoFixIssues, "ManualFixNeeded"),
                    new FlowEndpoint(manualFixRequired)),

                // 76. AUTO_FIX Error -> MANUAL_FIX_REQUIRED
                new(new FlowEndpoint(autoFixIssues, "Error"),
                    new FlowEndpoint(manualFixRequired)),

                // 77. MANUAL_FIX FixApplied -> QUALITY_GATE_CHECK (retry)
                new(new FlowEndpoint(manualFixRequired, "FixApplied"),
                    new FlowEndpoint(qualityGateCheck)),

                // 78. MANUAL_FIX NeedHelp -> PROVIDE_GUIDANCE
                new(new FlowEndpoint(manualFixRequired, "NeedHelp"),
                    new FlowEndpoint(provideGuidance)),

                // 79. MANUAL_FIX Error -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(manualFixRequired, "Error"),
                    new FlowEndpoint(diagnoseBlocker)),

                // =============================================================
                // REVIEW PATH (iteration max 5)
                // PREPARE -> Code Review sub-workflow -> MONITOR_REVIEW
                // MONITOR -> [Approved] -> MERGE
                // MONITOR -> [ChangesRequested] -> GUIDE_FIXES -> RE_REQUEST -> MONITOR
                // MONITOR -> [Pending] -> MONITOR (wait loop)
                // =============================================================

                // 80. PREPARE Prepared -> Code Review sub-workflow
                new(new FlowEndpoint(prepareCodeReview, "Prepared"),
                    new FlowEndpoint(codeReviewWorkflow)),

                // 81. Code Review sub-workflow -> MONITOR_REVIEW
                new(codeReviewWorkflow, monitorReview),

                // 82. PREPARE Error -> DIAGNOSE_BLOCKER
                new(new FlowEndpoint(prepareCodeReview, "Error"),
                    new FlowEndpoint(diagnoseBlocker)),

                // 83. MONITOR_REVIEW Approved -> MERGE_AND_COMPLETE
                new(new FlowEndpoint(monitorReview, "Approved"),
                    new FlowEndpoint(mergeAndComplete)),

                // 84. MONITOR_REVIEW ChangesRequested -> Increment Review Iteration
                new(new FlowEndpoint(monitorReview, "ChangesRequested"),
                    new FlowEndpoint(incrementReviewIteration)),

                // 85. Increment -> Guard Review Iterations
                new(incrementReviewIteration, guardReviewIterations),

                // 86. Guard True (iterations remaining) -> GUIDE_FIXES
                new(new FlowEndpoint(guardReviewIterations, "True"),
                    new FlowEndpoint(guideFixes)),

                // 87. Guard False (max iterations) -> MERGE_AND_COMPLETE (force merge)
                new(new FlowEndpoint(guardReviewIterations, "False"),
                    new FlowEndpoint(mergeAndComplete)),

                // 88. MONITOR_REVIEW Pending -> MONITOR_REVIEW (wait loop)
                new(new FlowEndpoint(monitorReview, "Pending"),
                    new FlowEndpoint(monitorReview)),

                // 89. MONITOR_REVIEW Error -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(monitorReview, "Error"),
                    new FlowEndpoint(escalateToSenior)),

                // 90. GUIDE_FIXES Guided -> RE_REQUEST_REVIEW
                new(new FlowEndpoint(guideFixes, "Guided"),
                    new FlowEndpoint(reRequestReview)),

                // 91. GUIDE_FIXES Error -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(guideFixes, "Error"),
                    new FlowEndpoint(escalateToSenior)),

                // 92. RE_REQUEST ReviewRequested -> MONITOR_REVIEW (back to monitoring)
                new(new FlowEndpoint(reRequestReview, "ReviewRequested"),
                    new FlowEndpoint(monitorReview)),

                // 93. RE_REQUEST MaxRetries -> MERGE_AND_COMPLETE (force merge)
                new(new FlowEndpoint(reRequestReview, "MaxRetries"),
                    new FlowEndpoint(mergeAndComplete)),

                // 94. RE_REQUEST Error -> ESCALATE_TO_SENIOR
                new(new FlowEndpoint(reRequestReview, "Error"),
                    new FlowEndpoint(escalateToSenior)),

                // =============================================================
                // COMPLETION PATH
                // MERGE -> REPORT -> PROFILE -> COMPLETED
                // =============================================================

                // 95. MERGE Merged -> GENERATE_REPORT
                new(new FlowEndpoint(mergeAndComplete, "Merged"),
                    new FlowEndpoint(generateReport)),

                // 96. MERGE Error -> FAILED
                new(new FlowEndpoint(mergeAndComplete, "Error"),
                    new FlowEndpoint(failed)),

                // 97. GENERATE_REPORT Generated -> UPDATE_SKILL_PROFILE
                new(new FlowEndpoint(generateReport, "Generated"),
                    new FlowEndpoint(updateSkillProfile)),

                // 98. GENERATE_REPORT Error -> COMPLETED (still complete, just no report)
                new(new FlowEndpoint(generateReport, "Error"),
                    new FlowEndpoint(completed)),

                // 99. UPDATE_SKILL_PROFILE Updated -> COMPLETED
                new(new FlowEndpoint(updateSkillProfile, "Updated"),
                    new FlowEndpoint(completed)),

                // 100. UPDATE_SKILL_PROFILE Error -> COMPLETED (still complete)
                new(new FlowEndpoint(updateSkillProfile, "Error"),
                    new FlowEndpoint(completed)),
            }
        };
    }
}
