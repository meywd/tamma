using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Assessment;
using Tamma.Activities.Assessment.Models;
using Tamma.Core.Enums;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Assessment sub-workflow that evaluates a junior developer's understanding
/// of story requirements through AI-generated questions, response analysis
/// (via bookmark wait), and skill profiling.
///
/// Flow:
///   1. Gather context (via Context Gathering 7-1F)
///   2. Generate targeted questions (via LLM Call 7-1B)
///   3. Deliver questions to junior
///   4. Wait for response (bookmark-based with timeout)
///   5a. On response: analyze with AI, classify result, update profile
///   5b. On timeout: set timeout result, update profile
///   6. Set workflow outputs
///
/// Can be invoked standalone via ELSA REST API or as a child workflow via RunWorkflow.
/// </summary>
public class AssessmentWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Assessment";
        builder.DefinitionId = "assessment";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Evaluate junior developer's understanding of story requirements";

        // ── Workflow variables ──────────────────────────────────────────
        var sessionId = builder.WithVariable<Guid>();
        var storyId = builder.WithVariable<string>();
        var juniorId = builder.WithVariable<string>();
        var skillLevel = builder.WithVariable<int>();
        var previousAttemptJson = builder.WithVariable<string>();
        var storyContext = builder.WithVariable<string>();
        var questionsJson = builder.WithVariable<string>();
        var juniorResponse = builder.WithVariable<string>();
        var analysisResultJson = builder.WithVariable<string>();
        var responseReceived = builder.WithVariable<bool>();
        var assessmentStatus = builder.WithVariable<AssessmentOutcomeStatus>();
        var confidence = builder.WithVariable<decimal>();
        var nextState = builder.WithVariable<MentorshipState>();
        var gapsJson = builder.WithVariable<string>();
        var strengthsJson = builder.WithVariable<string>();
        var attemptNumber = builder.WithVariable<int>();

        // Variables to capture activity outputs via binding
        var generatedQuestionSet = builder.WithVariable<QuestionSet>();
        var deliveryResult = builder.WithVariable<DeliveryResult>();
        var waitJuniorResponse = builder.WithVariable<string>();
        var waitResponseReceived = builder.WithVariable<bool>();
        var analysisOutput = builder.WithVariable<AnalysisResult>();
        var classifiedStatus = builder.WithVariable<AssessmentOutcomeStatus>();
        var classifiedConfidence = builder.WithVariable<decimal>();
        var classifiedNextState = builder.WithVariable<MentorshipState>();

        // Output variables (readable by parent workflow)
        var outputResultJson = builder.WithVariable<string>();
        var outputNextState = builder.WithVariable<string>();
        var outputStatus = builder.WithVariable<string>();
        var outputSkillLevel = builder.WithVariable<int>();

        // ── Step 1: Read inputs into variables ─────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs",
            Name = "Read Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                var sid = context.GetInput<Guid>("sessionId");
                storyId.Set(context, context.GetInput<string>("storyId") ?? string.Empty);
                juniorId.Set(context, context.GetInput<string>("juniorId") ?? string.Empty);
                skillLevel.Set(context, context.GetInput<int>("skillLevel"));
                previousAttemptJson.Set(context, context.GetInput<string>("previousAttemptJson") ?? string.Empty);

                var prevJson = context.GetInput<string>("previousAttemptJson") ?? string.Empty;
                if (!string.IsNullOrEmpty(prevJson))
                {
                    try
                    {
                        var prev = JsonSerializer.Deserialize<PreviousAttempt>(prevJson);
                        attemptNumber.Set(context, (prev?.AttemptNumber ?? 0) + 1);
                    }
                    catch
                    {
                        attemptNumber.Set(context, 1);
                    }
                }
                else
                {
                    attemptNumber.Set(context, 1);
                }
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Gather context via ContextGathering workflow (7-1F) ─
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext",
            Name = "Gather Context",
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
        gatherContext.SetDisplayText("Gather Context");

        var storeContextResult = new SetVariable
        {
            Id = "StoreContextResult",
            Name = "Store Context Result",
            Variable = storyContext,
            Value = new(ctx => {
                // Context is available from the dispatched workflow output
                return $"Assessment context for story {storyId.Get(ctx)} gathered via ContextGathering workflow";
            })
        };
        storeContextResult.SetDisplayText("Store Context Result");

        // ── Step 3: Generate questions ─────────────────────────────────
        var generateQuestions = new GenerateQuestionsActivity
        {
            Id = "GenerateQuestions",
            Name = "Generate Questions",
            SessionId = new(context => sessionId.Get(context)),
            StoryId = new(context => storyId.Get(context)),
            SkillLevel = new(context => skillLevel.Get(context)),
            StoryContext = new(context => storyContext.Get(context)),
            PreviousAttemptJson = new(context => previousAttemptJson.Get(context)),
            Result = new(generatedQuestionSet)
        };
        generateQuestions.SetDisplayText("Generate Questions");

        // Store generated questions as JSON string
        var storeQuestions = new SetVariable
        {
            Id = "StoreQuestions",
            Name = "Store Questions",
            Variable = questionsJson,
            Value = new(context =>
            {
                var qs = generatedQuestionSet.Get(context);
                return qs != null ? JsonSerializer.Serialize(qs) : "{}";
            })
        };
        storeQuestions.SetDisplayText("Store Questions");

        // ── Step 4: Deliver questions ──────────────────────────────────
        var deliverQuestions = new DeliverQuestionsActivity
        {
            Id = "DeliverQuestions",
            Name = "Deliver Questions",
            SessionId = new(context => sessionId.Get(context)),
            JuniorId = new(context => juniorId.Get(context)),
            QuestionsJson = new(context => questionsJson.Get(context)),
            AttemptNumber = new(context => attemptNumber.Get(context)),
            Result = new(deliveryResult)
        };
        deliverQuestions.SetDisplayText("Deliver Questions");

        // ── Step 5: Wait for response (bookmark) ──────────────────────
        var waitForResponse = new WaitForResponseActivity
        {
            Id = "WaitForResponse",
            Name = "Wait For Response",
            SessionId = new(context => sessionId.Get(context)),
            AttemptNumber = new(context => attemptNumber.Get(context)),
            SkillLevel = new(context => skillLevel.Get(context)),
            JuniorResponse = new(waitJuniorResponse),
            ResponseReceived = new(waitResponseReceived)
        };
        waitForResponse.SetDisplayText("Wait For Response");

        // ── Step 6a: Store response into workflow variable ─────────────
        var storeResponse = new SetVariable
        {
            Id = "StoreResponse",
            Name = "Store Response",
            Variable = juniorResponse,
            Value = new(context =>
            {
                responseReceived.Set(context, true);
                return waitJuniorResponse.Get(context) ?? string.Empty;
            })
        };
        storeResponse.SetDisplayText("Store Response");

        // ── Step 6a: Analyze response ──────────────────────────────────
        var analyzeResponse = new AnalyzeResponseActivity
        {
            Id = "AnalyzeResponse",
            Name = "Analyze Response",
            SessionId = new(context => sessionId.Get(context)),
            SkillLevel = new(context => skillLevel.Get(context)),
            QuestionsJson = new(context => questionsJson.Get(context)),
            JuniorResponse = new(context => juniorResponse.Get(context)),
            StoryContext = new(context => storyContext.Get(context)),
            Result = new(analysisOutput)
        };
        analyzeResponse.SetDisplayText("Analyze Response");

        // Store analysis result as JSON + extract gaps/strengths
        var storeAnalysis = new SetVariable
        {
            Id = "StoreAnalysis",
            Name = "Store Analysis",
            Variable = analysisResultJson,
            Value = new(context =>
            {
                var result = analysisOutput.Get(context);
                if (result != null)
                {
                    gapsJson.Set(context, JsonSerializer.Serialize(result.Gaps));
                    strengthsJson.Set(context, JsonSerializer.Serialize(result.Strengths));
                }
                return result != null ? JsonSerializer.Serialize(result) : "{}";
            })
        };
        storeAnalysis.SetDisplayText("Store Analysis");

        // ── Step 6b: Handle timeout ────────────────────────────────────
        var setTimeoutResult = new SetVariable
        {
            Id = "SetTimeoutResult",
            Name = "Set Timeout Result",
            Variable = analysisResultJson,
            Value = new(context =>
            {
                responseReceived.Set(context, false);
                assessmentStatus.Set(context, AssessmentOutcomeStatus.Timeout);
                confidence.Set(context, 0m);
                nextState.Set(context, MentorshipState.DIAGNOSE_BLOCKER);
                gapsJson.Set(context, "[]");
                strengthsJson.Set(context, "[]");
                juniorResponse.Set(context, string.Empty);
                return JsonSerializer.Serialize(new AnalysisResult
                {
                    Status = AssessmentOutcomeStatus.Timeout,
                    Confidence = 0m,
                    Gaps = new List<string> { "No response received within timeout window" },
                    Strengths = new List<string>(),
                    Rationale = "Assessment timed out - no response from junior",
                    UnderstandingSummary = "Unable to assess - no response received"
                });
            })
        };
        setTimeoutResult.SetDisplayText("Set Timeout Result");

        // ── Step 7: Classify result ────────────────────────────────────
        var classifyResult = new ClassifyResultActivity
        {
            Id = "ClassifyResult",
            Name = "Classify Result",
            AnalysisResultJson = new(context => analysisResultJson.Get(context)),
            ResponseReceived = new(context => responseReceived.Get(context)),
            Status = new(classifiedStatus),
            Confidence = new(classifiedConfidence),
            NextState = new(classifiedNextState)
        };
        classifyResult.SetDisplayText("Classify Result");

        // Store classification outputs into workflow variables
        var storeClassification = new SetVariable
        {
            Id = "StoreClassification",
            Name = "Store Classification",
            Variable = assessmentStatus,
            Value = new(context =>
            {
                confidence.Set(context, classifiedConfidence.Get(context));
                nextState.Set(context, classifiedNextState.Get(context));
                return classifiedStatus.Get(context);
            })
        };
        storeClassification.SetDisplayText("Store Classification");

        // ── Step 8: Update skill profile (response path) ──────────────
        var updateSkillProfile = new UpdateSkillProfileActivity
        {
            Id = "UpdateSkillProfile",
            Name = "Update Skill Profile",
            SessionId = new(context => sessionId.Get(context)),
            JuniorId = new(context => juniorId.Get(context)),
            StoryId = new(context => storyId.Get(context)),
            Status = new(context => assessmentStatus.Get(context)),
            Confidence = new(context => confidence.Get(context)),
            GapsJson = new(context => gapsJson.Get(context)),
            StrengthsJson = new(context => strengthsJson.Get(context))
        };
        updateSkillProfile.SetDisplayText("Update Skill Profile");

        // Separate instance for timeout path
        var updateSkillProfileTimeout = new UpdateSkillProfileActivity
        {
            Id = "UpdateSkillProfileTimeout",
            Name = "Update Skill Profile (Timeout)",
            SessionId = new(context => sessionId.Get(context)),
            JuniorId = new(context => juniorId.Get(context)),
            StoryId = new(context => storyId.Get(context)),
            Status = new(context => assessmentStatus.Get(context)),
            Confidence = new(context => confidence.Get(context)),
            GapsJson = new(context => gapsJson.Get(context)),
            StrengthsJson = new(context => strengthsJson.Get(context))
        };
        updateSkillProfileTimeout.SetDisplayText("Update Skill Profile (Timeout)");

        // ── Step 9: Set workflow output (response path) ────────────────
        // Store the final result into output variables for parent workflow retrieval
        var setOutput = new SetVariable
        {
            Id = "SetOutputResult",
            Name = "Set Output Result",
            Variable = outputResultJson,
            Value = new(context =>
            {
                var result = new AssessmentResult
                {
                    Status = assessmentStatus.Get(context),
                    Confidence = confidence.Get(context),
                    NextState = nextState.Get(context),
                    Questions = DeserializeList(questionsJson.Get(context)),
                    JuniorResponse = juniorResponse.Get(context) ?? string.Empty,
                    Gaps = DeserializeList(gapsJson.Get(context)),
                    Strengths = DeserializeList(strengthsJson.Get(context)),
                    AnalysisRationale = "Assessment completed"
                };
                outputNextState.Set(context, nextState.Get(context).ToString());
                outputStatus.Set(context, assessmentStatus.Get(context).ToString());
                // Map confidence to skill level 1-5
                var conf = confidence.Get(context);
                int assessed = conf >= 0.8m ? 5 : conf >= 0.6m ? 4 : conf >= 0.4m ? 3 : conf >= 0.2m ? 2 : 1;
                outputSkillLevel.Set(context, assessed);
                return JsonSerializer.Serialize(result);
            })
        };
        setOutput.SetDisplayText("Set Output Result");

        // Set workflow output (timeout path)
        var setOutputTimeout = new SetVariable
        {
            Id = "SetOutputTimeout",
            Name = "Set Output Timeout",
            Variable = outputResultJson,
            Value = new(context =>
            {
                var result = new AssessmentResult
                {
                    Status = AssessmentOutcomeStatus.Timeout,
                    Confidence = 0m,
                    NextState = MentorshipState.DIAGNOSE_BLOCKER,
                    Questions = DeserializeList(questionsJson.Get(context)),
                    JuniorResponse = string.Empty,
                    Gaps = new List<string> { "No response received" },
                    Strengths = new List<string>(),
                    AnalysisRationale = "Assessment timed out"
                };
                outputNextState.Set(context, MentorshipState.DIAGNOSE_BLOCKER.ToString());
                outputStatus.Set(context, AssessmentOutcomeStatus.Timeout.ToString());
                outputSkillLevel.Set(context, 1); // Timeout implies lowest skill level
                return JsonSerializer.Serialize(result);
            })
        };
        setOutputTimeout.SetDisplayText("Set Output Timeout");

        // ── Step 10: Expose outputs via SetOutput for parent consumption ─
        var exposeOutputResponse = new Sequence
        {
            Id = "ExposeOutputResponse",
            Name = "Expose Output Response",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputAssessmentResult", Name = "Output Assessment Result", OutputName = new("assessmentResult"), OutputValue = new(ctx => (object)(outputResultJson.Get(ctx) ?? "{}")) }, "Output Assessment Result"),
                WithLabel(new SetOutput { Id = "OutputNextState", Name = "Output Next State", OutputName = new("nextState"), OutputValue = new(ctx => (object)(outputNextState.Get(ctx) ?? "")) }, "Output Next State"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputSkillLevel", Name = "Output Skill Level", OutputName = new("skillLevel"), OutputValue = new(ctx => (object)outputSkillLevel.Get(ctx)) }, "Output Skill Level")
            }
        };
        exposeOutputResponse.SetDisplayText("Expose Output Response");
        var exposeOutputTimeout = new Sequence
        {
            Id = "ExposeOutputTimeout",
            Name = "Expose Output Timeout",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputAssessmentResultTimeout", Name = "Output Assessment Result (Timeout)", OutputName = new("assessmentResult"), OutputValue = new(ctx => (object)(outputResultJson.Get(ctx) ?? "{}")) }, "Output Assessment Result (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputNextStateTimeout", Name = "Output Next State (Timeout)", OutputName = new("nextState"), OutputValue = new(ctx => (object)(outputNextState.Get(ctx) ?? "")) }, "Output Next State (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputStatusTimeout", Name = "Output Status (Timeout)", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputSkillLevelTimeout", Name = "Output Skill Level (Timeout)", OutputName = new("skillLevel"), OutputValue = new(ctx => (object)outputSkillLevel.Get(ctx)) }, "Output Skill Level (Timeout)")
            }
        };
        exposeOutputTimeout.SetDisplayText("Expose Output Timeout");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "AssessmentFlowchart",
            Name = "Assessment Flowchart",
            Activities =
            {
                readInputs,
                gatherContext,
                storeContextResult,
                generateQuestions,
                storeQuestions,
                deliverQuestions,
                waitForResponse,
                storeResponse,
                analyzeResponse,
                storeAnalysis,
                classifyResult,
                storeClassification,
                updateSkillProfile,
                setOutput,
                exposeOutputResponse,
                setTimeoutResult,
                updateSkillProfileTimeout,
                setOutputTimeout,
                exposeOutputTimeout
            },
            Connections =
            {
                // Main flow: inputs -> context -> store context -> questions -> deliver -> wait
                new(readInputs, gatherContext),
                new(gatherContext, storeContextResult),
                new(storeContextResult, generateQuestions),
                new(generateQuestions, storeQuestions),
                new(storeQuestions, deliverQuestions),
                new(deliverQuestions, waitForResponse),

                // Response path: wait[Responded] -> store -> analyze -> classify -> profile -> output
                new(new FlowEndpoint(waitForResponse, "Responded"), new FlowEndpoint(storeResponse)),
                new(storeResponse, analyzeResponse),
                new(analyzeResponse, storeAnalysis),
                new(storeAnalysis, classifyResult),
                new(classifyResult, storeClassification),
                new(storeClassification, updateSkillProfile),
                new(updateSkillProfile, setOutput),
                new(setOutput, exposeOutputResponse),

                // Timeout path: wait[Timeout] -> timeout result -> profile -> output
                new(new FlowEndpoint(waitForResponse, "Timeout"), new FlowEndpoint(setTimeoutResult)),
                new(setTimeoutResult, updateSkillProfileTimeout),
                new(updateSkillProfileTimeout, setOutputTimeout),
                new(setOutputTimeout, exposeOutputTimeout)
            }
        };
    }

    /// <summary>
    /// Deserialize a JSON string to a list, returning empty list on failure
    /// </summary>
    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<string>();

        try
        {
            var questionSet = JsonSerializer.Deserialize<QuestionSet>(json);
            if (questionSet?.Questions.Count > 0)
                return questionSet.Questions;
        }
        catch { /* not a QuestionSet */ }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
