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
using Tamma.Api.Services.Agents;
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
///   2. Generate targeted questions via DispatchWorkflow("llm-call")
///      role=product_owner / action=generate-assessment-questions
///   3. Deliver questions to junior
///   4. Wait for response (bookmark-based with timeout)
///   5a. On response: analyse via DispatchWorkflow("llm-call")
///       role=product_owner / action=analyze-assessment-response,
///       classify result, update profile
///   5b. On timeout: set timeout result, update profile
///   6. Set workflow outputs
///
/// Fail-closed: if either llm-call returns success=false, or the JSON
/// response cannot be parsed into the expected shape, the workflow routes
/// to the LlmCallError terminal — it never proceeds with fabricated
/// questions or a fabricated confidence score.
///
/// Can be invoked standalone via the Elsa REST API or as a child workflow
/// via RunWorkflow.
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
        var sessionId        = builder.WithVariable<Guid>();
        var storyId          = builder.WithVariable<string>();
        var juniorId         = builder.WithVariable<string>();
        var skillLevel       = builder.WithVariable<int>();
        var previousAttemptJson = builder.WithVariable<string>();
        var tenantId         = builder.WithVariable<string>("TenantId", "");
        var storyContext     = builder.WithVariable<string>();
        var questionsJson    = builder.WithVariable<string>();
        var juniorResponse   = builder.WithVariable<string>();
        var analysisResultJson = builder.WithVariable<string>();
        var responseReceived = builder.WithVariable<bool>();
        var assessmentStatus = builder.WithVariable<AssessmentOutcomeStatus>();
        var confidence       = builder.WithVariable<decimal>();
        var nextState        = builder.WithVariable<MentorshipState>();
        var gapsJson         = builder.WithVariable<string>();
        var strengthsJson    = builder.WithVariable<string>();
        var attemptNumber    = builder.WithVariable<int>();

        // llm-call result containers
        var contextGatherResult = builder.WithVariable<IDictionary<string, object>?>();
        var questionLlm         = builder.WithVariable<IDictionary<string, object>?>();
        var analysisLlm         = builder.WithVariable<IDictionary<string, object>?>();

        // Success flags (fail-closed guards)
        var questionsLlmOk  = builder.WithVariable<bool>();
        var analysisLlmOk   = builder.WithVariable<bool>();

        // Variables to capture activity outputs via binding
        var generatedQuestionSet = builder.WithVariable<QuestionSet>();
        var deliveryResult       = builder.WithVariable<DeliveryResult>();
        var waitJuniorResponse   = builder.WithVariable<string>();
        var waitResponseReceived = builder.WithVariable<bool>();
        var analysisOutput       = builder.WithVariable<AnalysisResult>();
        var classifiedStatus     = builder.WithVariable<AssessmentOutcomeStatus>();
        var classifiedConfidence = builder.WithVariable<decimal>();
        var classifiedNextState  = builder.WithVariable<MentorshipState>();

        // Output variables (readable by parent workflow)
        var outputResultJson  = builder.WithVariable<string>();
        var outputNextState   = builder.WithVariable<string>();
        var outputStatus      = builder.WithVariable<string>();
        var outputSkillLevel  = builder.WithVariable<int>();

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
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);

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
            WaitForCompletion = new(true),
            Result = new(contextGatherResult)
        };
        gatherContext.SetDisplayText("Gather Context");

        var storeContextResult = new SetVariable
        {
            Id = "StoreContextResult",
            Name = "Store Context Result",
            Variable = storyContext,
            Value = new(ctx =>
            {
                var result = contextGatherResult.Get(ctx);
                if (result != null && result.TryGetValue("summary", out var s) && s != null)
                    return s.ToString() ?? $"Assessment context for story {storyId.Get(ctx)}";
                return $"Assessment context for story {storyId.Get(ctx)} gathered via ContextGathering workflow";
            })
        };
        storeContextResult.SetDisplayText("Store Context Result");

        // ── Step 3: Generate questions via llm-call ────────────────────
        var generateQuestionsLlm = new DispatchWorkflow
        {
            Id = "GenerateQuestionsLlm",
            Name = "Generate Questions (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"]      = AgentRole.ProductOwner.ToWire(),
                ["action"]    = AgentAction.GenerateAssessmentQuestions.ToWire(),
                ["tenantId"]  = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["storyContext"]  = storyContext.Get(ctx) ?? "",
                    ["skillLevel"]   = skillLevel.Get(ctx),
                    ["questionCount"] = ComputeQuestionCount(skillLevel.Get(ctx)),
                    ["previousGaps"] = ExtractPreviousGaps(previousAttemptJson.Get(ctx)),
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(questionLlm),
        };
        generateQuestionsLlm.SetDisplayText("Generate Questions (LLM)");

        // Parse llm-call response into QuestionSet; set questionsLlmOk flag
        var parseQuestionsResult = new SetVariable
        {
            Id = "ParseQuestionsResult",
            Name = "Parse Questions Result",
            Variable = generatedQuestionSet,
            Value = new(ctx =>
            {
                var result = questionLlm.Get(ctx);

                // Fail-closed: no result or success=false → error
                var succeeded = ReadSuccessFlag(result);
                if (!succeeded)
                {
                    questionsLlmOk.Set(ctx, false);
                    return null;
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                try
                {
                    // Try JSON array of strings: ["Q1","Q2",...]
                    var arrStart = text.IndexOf('[');
                    var arrEnd   = text.LastIndexOf(']');
                    if (arrStart >= 0 && arrEnd > arrStart)
                    {
                        var qs = JsonSerializer.Deserialize<List<string>>(text[arrStart..(arrEnd + 1)]);
                        if (qs?.Count > 0)
                        {
                            questionsLlmOk.Set(ctx, true);
                            return (object)new QuestionSet { Questions = qs, TargetSkillLevel = skillLevel.Get(ctx) };
                        }
                    }

                    // Try JSON object: {"questions":["Q1","Q2",...]}
                    var objStart = text.IndexOf('{');
                    var objEnd   = text.LastIndexOf('}');
                    if (objStart >= 0 && objEnd > objStart)
                    {
                        var qs = JsonSerializer.Deserialize<QuestionSet>(text[objStart..(objEnd + 1)]);
                        if (qs?.Questions?.Count > 0)
                        {
                            questionsLlmOk.Set(ctx, true);
                            return (object)qs;
                        }
                    }
                }
                catch { /* parse failure → fail closed below */ }

                questionsLlmOk.Set(ctx, false);
                return null;
            })
        };
        parseQuestionsResult.SetDisplayText("Parse Questions Result");

        // Fail-closed gate: route to error terminal if questions LLM call failed
        var questionsSuccessCheck = new FlowDecision(ctx => questionsLlmOk.Get(ctx))
        { Id = "QuestionsLlmOk", Name = "Questions LLM OK?" };
        questionsSuccessCheck.SetDisplayText("Questions LLM OK?");

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

        // ── Step 6a: Analyse response via llm-call ─────────────────────
        var analyzeResponseLlm = new DispatchWorkflow
        {
            Id = "AnalyzeResponseLlm",
            Name = "Analyze Response (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"]      = AgentRole.ProductOwner.ToWire(),
                ["action"]    = AgentAction.AnalyzeAssessmentResponse.ToWire(),
                ["tenantId"]  = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["storyContext"] = storyContext.Get(ctx) ?? "",
                    ["questions"]   = questionsJson.Get(ctx) ?? "",
                    ["response"]    = juniorResponse.Get(ctx) ?? "",
                    ["skillLevel"]  = skillLevel.Get(ctx),
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(analysisLlm),
        };
        analyzeResponseLlm.SetDisplayText("Analyze Response (LLM)");

        // Parse llm-call response into AnalysisResult; set analysisLlmOk flag.
        //
        // Uses JsonElement extraction rather than direct AnalysisResult deserialisation
        // to avoid an enum mismatch: the LLM prompt suggests "ready|needs_guidance|
        // not_ready" as status values, which do not map to AssessmentOutcomeStatus
        // (Correct|Partial|Incorrect|Timeout). ClassifyResultActivity recomputes Status
        // from the Confidence threshold anyway, so we only need to extract Confidence,
        // Rationale, Gaps, and Strengths robustly.
        var parseAnalysisResult = new SetVariable
        {
            Id = "ParseAnalysisResult",
            Name = "Parse Analysis Result",
            Variable = analysisOutput,
            Value = new(ctx =>
            {
                var result = analysisLlm.Get(ctx);

                // Fail-closed: no result or success=false → error
                var succeeded = ReadSuccessFlag(result);
                if (!succeeded)
                {
                    analysisLlmOk.Set(ctx, false);
                    return null;
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                try
                {
                    // Slice JSON object: {"confidence":0.8,"gaps":[...],...}
                    var jsonStart = text.IndexOf('{');
                    var jsonEnd   = text.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var element = JsonSerializer.Deserialize<JsonElement>(text[jsonStart..(jsonEnd + 1)]);

                        // Extract numeric confidence (fail if missing/unparseable)
                        if (!element.TryGetProperty("confidence", out var cv) ||
                            cv.ValueKind != JsonValueKind.Number)
                        {
                            analysisLlmOk.Set(ctx, false);
                            return null;
                        }

                        var parsed = new AnalysisResult
                        {
                            Confidence = (decimal)cv.GetDouble(),
                            Rationale  = element.TryGetProperty("rationale", out var rv) ? rv.GetString() ?? "" : "",
                            Gaps       = ExtractStringList(element, "gaps"),
                            Strengths  = ExtractStringList(element, "strengths"),
                            // Status is recomputed by ClassifyResultActivity from Confidence
                            Status = AssessmentOutcomeStatus.Incorrect,
                        };

                        analysisLlmOk.Set(ctx, true);
                        return (object)parsed;
                    }
                }
                catch { /* parse failure → fail closed below */ }

                analysisLlmOk.Set(ctx, false);
                return null;
            })
        };
        parseAnalysisResult.SetDisplayText("Parse Analysis Result");

        // Fail-closed gate: route to error terminal if analysis LLM call failed
        var analysisSuccessCheck = new FlowDecision(ctx => analysisLlmOk.Get(ctx))
        { Id = "AnalysisLlmOk", Name = "Analysis LLM OK?" };
        analysisSuccessCheck.SetDisplayText("Analysis LLM OK?");

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
        // Use the real LLM rationale from analysisOutput (P0 fix: was hardcoded
        // "Assessment completed" — now the actual reasoning from the LLM).
        var setOutput = new SetVariable
        {
            Id = "SetOutputResult",
            Name = "Set Output Result",
            Variable = outputResultJson,
            Value = new(context =>
            {
                var parsed = analysisOutput.Get(context);
                var result = new AssessmentResult
                {
                    Status = assessmentStatus.Get(context),
                    Confidence = confidence.Get(context),
                    NextState = nextState.Get(context),
                    Questions = DeserializeList(questionsJson.Get(context)),
                    JuniorResponse = juniorResponse.Get(context) ?? string.Empty,
                    Gaps = DeserializeList(gapsJson.Get(context)),
                    Strengths = DeserializeList(strengthsJson.Get(context)),
                    AnalysisRationale = parsed?.Rationale ?? "Assessment completed"
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

        // ── Fail-closed error terminal ─────────────────────────────────
        // Reached when either llm-call returns success=false or the JSON
        // cannot be parsed. The workflow terminates here rather than
        // proceeding with fabricated questions or a fabricated confidence.
        var llmCallError = new Finish
        {
            Id = "LlmCallError",
            Name = "LLM Call Error"
        };
        llmCallError.SetDisplayText("LLM Call Error");

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

                // Question generation (llm-call dispatch + parse + gate)
                generateQuestionsLlm,
                parseQuestionsResult,
                questionsSuccessCheck,

                storeQuestions,
                deliverQuestions,
                waitForResponse,
                storeResponse,

                // Response analysis (llm-call dispatch + parse + gate)
                analyzeResponseLlm,
                parseAnalysisResult,
                analysisSuccessCheck,

                storeAnalysis,
                classifyResult,
                storeClassification,
                updateSkillProfile,
                setOutput,
                exposeOutputResponse,

                // Timeout path
                setTimeoutResult,
                updateSkillProfileTimeout,
                setOutputTimeout,
                exposeOutputTimeout,

                // Fail-closed error terminal (both llm-call failures route here)
                llmCallError
            },
            Connections =
            {
                // Main flow: inputs → context → store context → generate questions
                new(readInputs, gatherContext),
                new(gatherContext, storeContextResult),
                new(storeContextResult, generateQuestionsLlm),

                // Question generation: dispatch → parse → gate
                new(generateQuestionsLlm, parseQuestionsResult),
                new(parseQuestionsResult, questionsSuccessCheck),
                new(new FlowEndpoint(questionsSuccessCheck, "True"),  new FlowEndpoint(storeQuestions)),
                new(new FlowEndpoint(questionsSuccessCheck, "False"), new FlowEndpoint(llmCallError)),

                // Deliver and wait
                new(storeQuestions, deliverQuestions),
                new(deliverQuestions, waitForResponse),

                // Response path: wait[Responded] → store → analyse → parse → gate
                new(new FlowEndpoint(waitForResponse, "Responded"), new FlowEndpoint(storeResponse)),
                new(storeResponse, analyzeResponseLlm),
                new(analyzeResponseLlm, parseAnalysisResult),
                new(parseAnalysisResult, analysisSuccessCheck),
                new(new FlowEndpoint(analysisSuccessCheck, "True"),  new FlowEndpoint(storeAnalysis)),
                new(new FlowEndpoint(analysisSuccessCheck, "False"), new FlowEndpoint(llmCallError)),

                // Classify → profile → output
                new(storeAnalysis, classifyResult),
                new(classifyResult, storeClassification),
                new(storeClassification, updateSkillProfile),
                new(updateSkillProfile, setOutput),
                new(setOutput, exposeOutputResponse),

                // Timeout path: wait[Timeout] → timeout result → profile → output
                new(new FlowEndpoint(waitForResponse, "Timeout"), new FlowEndpoint(setTimeoutResult)),
                new(setTimeoutResult, updateSkillProfileTimeout),
                new(updateSkillProfileTimeout, setOutputTimeout),
                new(setOutputTimeout, exposeOutputTimeout)
            }
        };
    }

    /// <summary>
    /// Read the <c>success</c> flag from a dispatched workflow's Result dictionary.
    /// Returns <c>false</c> if the dictionary is null, the key is absent, or the
    /// value is falsy — fail-closed by design.
    /// </summary>
    private static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return s switch
        {
            bool b    => b,
            string str => bool.TryParse(str, out var r) && r,
            _         => false,
        };
    }

    /// <summary>
    /// Compute the target question count for a given skill level.
    /// Mirrors the logic previously in GenerateQuestionsActivity.
    /// </summary>
    private static int ComputeQuestionCount(int skillLevel) => skillLevel switch
    {
        1 => 2,
        2 => 3,
        3 => 4,
        4 => 4,
        5 => 5,
        _ => 3
    };

    /// <summary>
    /// Extract the gap list from a previous-attempt JSON blob as a comma-separated
    /// string for use in the generate-assessment-questions prompt template.
    /// Returns an empty string when there is no previous attempt or no gaps.
    /// </summary>
    private static string ExtractPreviousGaps(string? previousAttemptJson)
    {
        if (string.IsNullOrEmpty(previousAttemptJson)) return "";
        try
        {
            var prev = JsonSerializer.Deserialize<PreviousAttempt>(previousAttemptJson);
            return prev?.Gaps?.Count > 0 ? string.Join(", ", prev.Gaps) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Extract a JSON array of strings from a <see cref="JsonElement"/> property.
    /// Returns an empty list when the property is absent, not an array, or empty.
    /// </summary>
    private static List<string> ExtractStringList(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    /// <summary>
    /// Deserialize a JSON string to a list of strings, returning an empty list on failure.
    /// Handles both plain JSON arrays and QuestionSet-wrapped arrays.
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
