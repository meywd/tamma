using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Context;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Plan Review — structured multi-agent debate with 3 phases.
///
/// Phase 1: Independent Review — 7 sequential role reviews (architect, developer, tester,
///          security, devops, product_owner, senior_developer). Each review is stored immediately.
///
/// Phase 2: Rebuttal Round — 7 sequential calls where each role sees ALL reviews (anonymized,
///          no role labels). Each role outputs responses to concerns and a revisedVerdict.
///          If all revisedVerdicts are "approve", early termination to approved output.
///
/// Phase 3: PO Decision — product_owner sees all reviews + rebuttals and decides:
///          approved → output, needsHuman → output, needsModification → update plan,
///          increment round, loop back to Phase 2 (max rounds from input, default 3).
///
/// Inputs: repository, issueNumber, planJson, contextIds, workItemJson, maxRetries
/// Outputs: decision, planJson, reviewNotes, deferred, split, discussionLog, suggestionsJson
/// </summary>
public class PlanReviewWorkflow : WorkflowBase
{
    // The 7 reviewing roles
    private static readonly string[] ReviewRoles =
    [
        "architect",
        "developer",
        "tester",
        "security",
        "devops",
        "product_owner",
        "senior_developer",
    ];

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Review";
        builder.DefinitionId = "plan-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Structured multi-agent debate: independent review, rebuttal round, PO decision";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var contextIds = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");

        // Per-role review results (JSON strings)
        var architectReview = builder.WithVariable<string>("ArchitectReview", "{}");
        var developerReview = builder.WithVariable<string>("DeveloperReview", "{}");
        var testerReview = builder.WithVariable<string>("TesterReview", "{}");
        var securityReview = builder.WithVariable<string>("SecurityReview", "{}");
        var devopsReview = builder.WithVariable<string>("DevOpsReview", "{}");
        var productOwnerReview = builder.WithVariable<string>("ProductOwnerReview", "{}");
        var seniorDeveloperReview = builder.WithVariable<string>("SeniorDeveloperReview", "{}");

        // Aggregation
        var allReviewsJson = builder.WithVariable<string>("AllReviewsJson", "[]");
        var anonymizedReviewsJson = builder.WithVariable<string>("AnonymizedReviewsJson", "[]");

        // Per-role rebuttal results (JSON strings)
        var architectRebuttal = builder.WithVariable<string>("ArchitectRebuttal", "{}");
        var developerRebuttal = builder.WithVariable<string>("DeveloperRebuttal", "{}");
        var testerRebuttal = builder.WithVariable<string>("TesterRebuttal", "{}");
        var securityRebuttal = builder.WithVariable<string>("SecurityRebuttal", "{}");
        var devopsRebuttal = builder.WithVariable<string>("DevOpsRebuttal", "{}");
        var productOwnerRebuttal = builder.WithVariable<string>("ProductOwnerRebuttal", "{}");
        var seniorDeveloperRebuttal = builder.WithVariable<string>("SeniorDeveloperRebuttal", "{}");

        var allRebuttalsJson = builder.WithVariable<string>("AllRebuttalsJson", "[]");

        // Discussion / rounds
        var roundCount = builder.WithVariable<int>("RoundCount", 0);
        var maxRounds = builder.WithVariable<int>("MaxRounds", 3);
        var discussionLog = builder.WithVariable<string>("DiscussionLog", "[]");
        var phase = builder.WithVariable<string>("Phase", "review");

        // Final outputs
        var decision = builder.WithVariable<string>("Decision", "needsHuman");
        var reviewNotes = builder.WithVariable<string>("ReviewNotes", "");
        var deferred = builder.WithVariable<string>("Deferred", "[]");
        var split = builder.WithVariable<string>("Split", "[]");
        var suggestionsJson = builder.WithVariable<string>("SuggestionsJson", "[]");

        // Shared LLM result
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // Early termination flag
        var allRebuttalApproved = builder.WithVariable<string>("AllRebuttalApproved", "false");

        // Role-variable mapping for Phase 1 extraction
        var roleReviewVariables = new Dictionary<string, Variable<string>>
        {
            ["architect"] = architectReview,
            ["developer"] = developerReview,
            ["tester"] = testerReview,
            ["security"] = securityReview,
            ["devops"] = devopsReview,
            ["product_owner"] = productOwnerReview,
            ["senior_developer"] = seniorDeveloperReview,
        };

        // Role-variable mapping for Phase 2 rebuttal extraction
        var roleRebuttalVariables = new Dictionary<string, Variable<string>>
        {
            ["architect"] = architectRebuttal,
            ["developer"] = developerRebuttal,
            ["tester"] = testerRebuttal,
            ["security"] = securityRebuttal,
            ["devops"] = devopsRebuttal,
            ["product_owner"] = productOwnerRebuttal,
            ["senior_developer"] = seniorDeveloperRebuttal,
        };

        // ================================================================
        // 1. Init — read inputs, set round count to 1
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                planJson.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                roundCount.Set(ctx, 1);
                var inputMaxRounds = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRounds.HasValue) maxRounds.Set(ctx, inputMaxRounds.Value);
                discussionLog.Set(ctx, "[]");
                phase.Set(ctx, "review");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // Phase 1: Independent Review — 7 sequential role reviews
        // Each role: action="plan-review", gets plan + context
        // After each extraction, persist the review via StoreRoleFindingActivity
        // ================================================================

        // Architect review
        var phase1ArchCall = RoleReviewDispatch("Phase1ArchReview", "Phase 1: Architect Review", "architect",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1Arch = ExtractReview(architectReview, llmResult, "architect",
            "ExtractPhase1Arch", "Extract Phase 1 Architect Review");
        var storePhase1Arch = StoreReviewRole("StorePhase1Arch", "Store Phase 1 Architect Review", "architect",
            repository, issueNumber, architectReview);

        // Developer review
        var phase1DevCall = RoleReviewDispatch("Phase1DevReview", "Phase 1: Developer Review", "developer",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1Dev = ExtractReview(developerReview, llmResult, "developer",
            "ExtractPhase1Dev", "Extract Phase 1 Developer Review");
        var storePhase1Dev = StoreReviewRole("StorePhase1Dev", "Store Phase 1 Developer Review", "developer",
            repository, issueNumber, developerReview);

        // Tester review
        var phase1TesterCall = RoleReviewDispatch("Phase1TesterReview", "Phase 1: Tester Review", "tester",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1Tester = ExtractReview(testerReview, llmResult, "tester",
            "ExtractPhase1Tester", "Extract Phase 1 Tester Review");
        var storePhase1Tester = StoreReviewRole("StorePhase1Tester", "Store Phase 1 Tester Review", "tester",
            repository, issueNumber, testerReview);

        // Security review
        var phase1SecCall = RoleReviewDispatch("Phase1SecReview", "Phase 1: Security Review", "security",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1Sec = ExtractReview(securityReview, llmResult, "security",
            "ExtractPhase1Sec", "Extract Phase 1 Security Review");
        var storePhase1Sec = StoreReviewRole("StorePhase1Sec", "Store Phase 1 Security Review", "security",
            repository, issueNumber, securityReview);

        // DevOps review
        var phase1DevOpsCall = RoleReviewDispatch("Phase1DevOpsReview", "Phase 1: DevOps Review", "devops",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1DevOps = ExtractReview(devopsReview, llmResult, "devops",
            "ExtractPhase1DevOps", "Extract Phase 1 DevOps Review");
        var storePhase1DevOps = StoreReviewRole("StorePhase1DevOps", "Store Phase 1 DevOps Review", "devops",
            repository, issueNumber, devopsReview);

        // Product Owner review
        var phase1POCall = RoleReviewDispatch("Phase1POReview", "Phase 1: PO Review", "product_owner",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1PO = ExtractReview(productOwnerReview, llmResult, "product_owner",
            "ExtractPhase1PO", "Extract Phase 1 PO Review");
        var storePhase1PO = StoreReviewRole("StorePhase1PO", "Store Phase 1 PO Review", "product_owner",
            repository, issueNumber, productOwnerReview);

        // Senior Developer review
        var phase1SrDevCall = RoleReviewDispatch("Phase1SrDevReview", "Phase 1: Senior Dev Review", "senior_developer",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPhase1SrDev = ExtractReview(seniorDeveloperReview, llmResult, "senior_developer",
            "ExtractPhase1SrDev", "Extract Phase 1 Senior Dev Review");
        var storePhase1SrDev = StoreReviewRole("StorePhase1SrDev", "Store Phase 1 Senior Dev Review", "senior_developer",
            repository, issueNumber, seniorDeveloperReview);

        // ================================================================
        // Aggregate Phase 1 Reviews — collect all reviews into allReviewsJson
        // and build anonymized version for Phase 2
        // ================================================================
        var aggregateReviews = new SetVariable
        {
            Id = "AggregateReviews", Name = "Aggregate Reviews",
            Variable = allReviewsJson,
            Value = new Input<object?>(ctx =>
            {
                var reviews = new List<object>();

                foreach (var role in ReviewRoles)
                {
                    var reviewJson = roleReviewVariables[role].Get(ctx);
                    var (verdict, comments, suggestedChanges) = ReviewAggregationHelper.ParseRoleVerdict(reviewJson);

                    reviews.Add(new Dictionary<string, object>
                    {
                        ["role"] = role,
                        ["verdict"] = verdict,
                        ["comments"] = comments,
                        ["suggestedChanges"] = suggestedChanges,
                    });
                }

                var reviewsArray = JsonSerializer.Serialize(reviews);

                // Append to discussion log
                var currentLog = discussionLog.Get(ctx);
                var logEntries = new List<object>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(currentLog) && currentLog != "[]")
                        logEntries = JsonSerializer.Deserialize<List<object>>(currentLog) ?? [];
                }
                catch { /* start fresh */ }

                var round = roundCount.Get(ctx);
                foreach (var review in reviews)
                {
                    logEntries.Add(new Dictionary<string, object>
                    {
                        ["round"] = round,
                        ["type"] = "phase1-review",
                        ["data"] = review,
                    });
                }
                discussionLog.Set(ctx, JsonSerializer.Serialize(logEntries));

                return (object)reviewsArray;
            })
        };
        aggregateReviews.SetDisplayText("Aggregate Phase 1 Reviews");

        // ================================================================
        // Build Anonymized Reviews for Phase 2
        // Strip role names, replace with reviewerIndex
        // ================================================================
        var buildAnonymized = new SetVariable
        {
            Id = "BuildAnonymized", Name = "Build Anonymized Reviews",
            Variable = anonymizedReviewsJson,
            Value = new Input<object?>(ctx =>
            {
                var allReviews = allReviewsJson.Get(ctx);
                var anonymized = new List<object>();

                try
                {
                    if (!string.IsNullOrWhiteSpace(allReviews) && allReviews != "[]")
                    {
                        using var doc = JsonDocument.Parse(allReviews);
                        var index = 1;
                        foreach (var review in doc.RootElement.EnumerateArray())
                        {
                            var entry = new Dictionary<string, object>
                            {
                                ["reviewerIndex"] = index,
                            };
                            if (review.TryGetProperty("verdict", out var v))
                                entry["verdict"] = v.GetString() ?? "concerns";
                            if (review.TryGetProperty("comments", out var c))
                                entry["comments"] = c.GetString() ?? "";
                            if (review.TryGetProperty("suggestedChanges", out var s))
                                entry["suggestedChanges"] = s.GetString() ?? "";

                            anonymized.Add(entry);
                            index++;
                        }
                    }
                }
                catch { /* fallback to empty */ }

                return (object)JsonSerializer.Serialize(anonymized);
            })
        };
        buildAnonymized.SetDisplayText("Build Anonymized Reviews");

        // ================================================================
        // Phase 2: Rebuttal Round — 7 sequential calls, each role sees
        // ALL reviews (anonymized) and their own Phase 1 review
        // ================================================================

        // Architect rebuttal
        var phase2ArchCall = RebuttalDispatch("Phase2ArchRebuttal", "Phase 2: Architect Rebuttal", "architect",
            repository, planJson, contextIds, anonymizedReviewsJson, architectReview, roundCount, llmResult);
        var extractPhase2Arch = ExtractRebuttal(architectRebuttal, llmResult,
            "ExtractPhase2Arch", "Extract Phase 2 Architect Rebuttal");
        var storePhase2Arch = StoreReviewRole("StorePhase2Arch", "Store Phase 2 Architect Rebuttal", "architect-rebuttal",
            repository, issueNumber, architectRebuttal);

        // Developer rebuttal
        var phase2DevCall = RebuttalDispatch("Phase2DevRebuttal", "Phase 2: Developer Rebuttal", "developer",
            repository, planJson, contextIds, anonymizedReviewsJson, developerReview, roundCount, llmResult);
        var extractPhase2Dev = ExtractRebuttal(developerRebuttal, llmResult,
            "ExtractPhase2Dev", "Extract Phase 2 Developer Rebuttal");
        var storePhase2Dev = StoreReviewRole("StorePhase2Dev", "Store Phase 2 Developer Rebuttal", "developer-rebuttal",
            repository, issueNumber, developerRebuttal);

        // Tester rebuttal
        var phase2TesterCall = RebuttalDispatch("Phase2TesterRebuttal", "Phase 2: Tester Rebuttal", "tester",
            repository, planJson, contextIds, anonymizedReviewsJson, testerReview, roundCount, llmResult);
        var extractPhase2Tester = ExtractRebuttal(testerRebuttal, llmResult,
            "ExtractPhase2Tester", "Extract Phase 2 Tester Rebuttal");
        var storePhase2Tester = StoreReviewRole("StorePhase2Tester", "Store Phase 2 Tester Rebuttal", "tester-rebuttal",
            repository, issueNumber, testerRebuttal);

        // Security rebuttal
        var phase2SecCall = RebuttalDispatch("Phase2SecRebuttal", "Phase 2: Security Rebuttal", "security",
            repository, planJson, contextIds, anonymizedReviewsJson, securityReview, roundCount, llmResult);
        var extractPhase2Sec = ExtractRebuttal(securityRebuttal, llmResult,
            "ExtractPhase2Sec", "Extract Phase 2 Security Rebuttal");
        var storePhase2Sec = StoreReviewRole("StorePhase2Sec", "Store Phase 2 Security Rebuttal", "security-rebuttal",
            repository, issueNumber, securityRebuttal);

        // DevOps rebuttal
        var phase2DevOpsCall = RebuttalDispatch("Phase2DevOpsRebuttal", "Phase 2: DevOps Rebuttal", "devops",
            repository, planJson, contextIds, anonymizedReviewsJson, devopsReview, roundCount, llmResult);
        var extractPhase2DevOps = ExtractRebuttal(devopsRebuttal, llmResult,
            "ExtractPhase2DevOps", "Extract Phase 2 DevOps Rebuttal");
        var storePhase2DevOps = StoreReviewRole("StorePhase2DevOps", "Store Phase 2 DevOps Rebuttal", "devops-rebuttal",
            repository, issueNumber, devopsRebuttal);

        // Product Owner rebuttal
        var phase2POCall = RebuttalDispatch("Phase2PORebuttal", "Phase 2: PO Rebuttal", "product_owner",
            repository, planJson, contextIds, anonymizedReviewsJson, productOwnerReview, roundCount, llmResult);
        var extractPhase2PO = ExtractRebuttal(productOwnerRebuttal, llmResult,
            "ExtractPhase2PO", "Extract Phase 2 PO Rebuttal");
        var storePhase2PO = StoreReviewRole("StorePhase2PO", "Store Phase 2 PO Rebuttal", "product_owner-rebuttal",
            repository, issueNumber, productOwnerRebuttal);

        // Senior Developer rebuttal
        var phase2SrDevCall = RebuttalDispatch("Phase2SrDevRebuttal", "Phase 2: Senior Dev Rebuttal", "senior_developer",
            repository, planJson, contextIds, anonymizedReviewsJson, seniorDeveloperReview, roundCount, llmResult);
        var extractPhase2SrDev = ExtractRebuttal(seniorDeveloperRebuttal, llmResult,
            "ExtractPhase2SrDev", "Extract Phase 2 Senior Dev Rebuttal");
        var storePhase2SrDev = StoreReviewRole("StorePhase2SrDev", "Store Phase 2 Senior Dev Rebuttal", "senior_developer-rebuttal",
            repository, issueNumber, seniorDeveloperRebuttal);

        // ================================================================
        // Aggregate Rebuttals + Check Early Termination
        // ================================================================
        var aggregateRebuttals = new SetVariable
        {
            Id = "AggregateRebuttals", Name = "Aggregate Rebuttals",
            Variable = allRebuttalsJson,
            Value = new Input<object?>(ctx =>
            {
                var rebuttals = new List<object>();
                var allApprove = true;

                foreach (var role in ReviewRoles)
                {
                    var rebuttalJson = roleRebuttalVariables[role].Get(ctx);
                    var rebuttalEntry = new Dictionary<string, object>
                    {
                        ["role"] = role,
                        ["rebuttal"] = rebuttalJson,
                    };
                    rebuttals.Add(rebuttalEntry);

                    // Check revisedVerdict
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(rebuttalJson) && rebuttalJson != "{}")
                        {
                            using var doc = JsonDocument.Parse(rebuttalJson);
                            if (doc.RootElement.TryGetProperty("revisedVerdict", out var rv))
                            {
                                var verdict = rv.GetString() ?? "";
                                if (verdict != "approve")
                                    allApprove = false;
                            }
                            else
                            {
                                allApprove = false;
                            }
                        }
                        else
                        {
                            allApprove = false;
                        }
                    }
                    catch
                    {
                        allApprove = false;
                    }
                }

                allRebuttalApproved.Set(ctx, allApprove ? "true" : "false");

                var rebuttalsArray = JsonSerializer.Serialize(rebuttals);

                // Append to discussion log
                var currentLog = discussionLog.Get(ctx);
                var logEntries = new List<object>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(currentLog) && currentLog != "[]")
                        logEntries = JsonSerializer.Deserialize<List<object>>(currentLog) ?? [];
                }
                catch { /* start fresh */ }

                var round = roundCount.Get(ctx);
                foreach (var rebuttal in rebuttals)
                {
                    logEntries.Add(new Dictionary<string, object>
                    {
                        ["round"] = round,
                        ["type"] = "phase2-rebuttal",
                        ["data"] = rebuttal,
                    });
                }
                discussionLog.Set(ctx, JsonSerializer.Serialize(logEntries));

                return (object)rebuttalsArray;
            })
        };
        aggregateRebuttals.SetDisplayText("Aggregate Rebuttals");

        // ================================================================
        // Early Termination Check — all revisedVerdict == "approve"?
        // ================================================================
        var earlyTermination = new FlowDecision(ctx => allRebuttalApproved.Get(ctx) == "true")
        { Id = "EarlyTermination", Name = "All Rebuttals Approve?" };
        earlyTermination.SetDisplayText("All Rebuttals Approve?");

        // ================================================================
        // Set Approved (from early termination)
        // ================================================================
        var setApprovedEarly = new SetVariable
        {
            Id = "SetApprovedEarly", Name = "Set Approved (Early)",
            Variable = decision,
            Value = new Input<object?>(ctx =>
            {
                reviewNotes.Set(ctx, "All 7 reviewers approved the plan during rebuttal round (unanimous consensus).");
                return (object)"approved";
            })
        };
        setApprovedEarly.SetDisplayText("Set Approved (Early Consensus)");

        // ================================================================
        // Phase 3: PO Decision — product_owner sees all reviews + rebuttals
        // ================================================================
        var phase3PODecisionCall = new DispatchWorkflow
        {
            Id = "Phase3PODecision", Name = "Phase 3: PO Decision",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "product_owner",
                ["action"] = "plan-review",
                ["variables"] = new Dictionary<string, object>
                {
                    ["planJson"] = planJson.Get(ctx),
                    ["allReviews"] = allReviewsJson.Get(ctx),
                    ["allRebuttals"] = allRebuttalsJson.Get(ctx),
                    ["phase"] = "po-decision",
                    ["roundNumber"] = roundCount.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        phase3PODecisionCall.SetDisplayText("Phase 3: PO Decision");

        // ================================================================
        // Extract PO Decision
        // ================================================================
        var extractPODecision = new SetVariable
        {
            Id = "ExtractPODecision", Name = "Extract PO Decision",
            Variable = decision,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                var output = "";
                if (result != null && result.TryGetValue("llmResponse", out var r))
                    output = r?.ToString() ?? "";

                // Try to extract JSON from the response
                var poDecision = "needsHuman";
                var suggestions = "[]";
                var modifiedPlan = "";
                var notes = "";

                try
                {
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var extracted = output[jsonStart..(jsonEnd + 1)];
                        using var doc = JsonDocument.Parse(extracted);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("decision", out var d))
                            poDecision = d.GetString() ?? "needsHuman";
                        if (root.TryGetProperty("suggestions", out var s))
                            suggestions = s.GetRawText();
                        if (root.TryGetProperty("modifiedPlan", out var mp))
                        {
                            modifiedPlan = mp.ValueKind == JsonValueKind.String
                                ? mp.GetString() ?? ""
                                : mp.GetRawText();
                        }
                        if (root.TryGetProperty("notes", out var n))
                            notes = n.GetString() ?? "";

                        // Also check for deferred/split in PO output
                        if (root.TryGetProperty("deferred", out var def))
                            deferred.Set(ctx, def.GetRawText());
                        if (root.TryGetProperty("split", out var sp))
                            split.Set(ctx, sp.GetRawText());
                    }
                }
                catch
                {
                    poDecision = "needsHuman";
                    notes = $"Failed to parse PO decision: {output}";
                }

                suggestionsJson.Set(ctx, suggestions);
                reviewNotes.Set(ctx, notes);

                // If needsModification and there's a modifiedPlan, update planJson
                if (poDecision == "needsModification" && !string.IsNullOrWhiteSpace(modifiedPlan))
                    planJson.Set(ctx, modifiedPlan);

                // Append to discussion log
                var currentLog = discussionLog.Get(ctx);
                var logEntries = new List<object>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(currentLog) && currentLog != "[]")
                        logEntries = JsonSerializer.Deserialize<List<object>>(currentLog) ?? [];
                }
                catch { /* start fresh */ }

                var round = roundCount.Get(ctx);
                logEntries.Add(new Dictionary<string, object>
                {
                    ["round"] = round,
                    ["type"] = "phase3-po-decision",
                    ["decision"] = poDecision,
                    ["notes"] = notes,
                    ["suggestions"] = suggestions,
                });
                discussionLog.Set(ctx, JsonSerializer.Serialize(logEntries));

                return (object)poDecision;
            })
        };
        extractPODecision.SetDisplayText("Extract PO Decision");

        // Store PO decision
        var storePODecisionActivity = new StoreRoleFindingActivity
        {
            Id = "StorePODecision", Name = "Store PO Decision",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Role = new Input<string>(ctx => $"po-decision-round-{roundCount.Get(ctx)}"),
            FindingsJson = new Input<string>(ctx =>
            {
                var decisionData = new Dictionary<string, object>
                {
                    ["decision"] = decision.Get(ctx),
                    ["notes"] = reviewNotes.Get(ctx),
                    ["suggestions"] = suggestionsJson.Get(ctx),
                };
                return JsonSerializer.Serialize(decisionData);
            }),
            ContextId = new Output<string>(new Variable<string>()),
        };
        storePODecisionActivity.SetDisplayText("Store PO Decision");

        // ================================================================
        // PO Decision Routing
        // ================================================================

        // Check: approved?
        var poApprovedCheck = new FlowDecision(ctx => decision.Get(ctx) == "approved")
        { Id = "POApprovedCheck", Name = "PO Approved?" };
        poApprovedCheck.SetDisplayText("PO: Approved?");

        // Check: needsHuman?
        var poNeedsHumanCheck = new FlowDecision(ctx => decision.Get(ctx) == "needsHuman")
        { Id = "PONeedsHumanCheck", Name = "PO Needs Human?" };
        poNeedsHumanCheck.SetDisplayText("PO: Needs Human?");

        // needsModification path: increment round, check max
        var incrementRound = new SetVariable
        {
            Id = "IncrRound", Name = "Increment Round",
            Variable = roundCount,
            Value = new Input<object?>(ctx => (object)(roundCount.Get(ctx) + 1))
        };
        incrementRound.SetDisplayText("Increment Round");

        var canContinue = new FlowDecision(ctx => roundCount.Get(ctx) <= maxRounds.Get(ctx))
        { Id = "CanContinue", Name = "Round <= Max?" };
        canContinue.SetDisplayText("Round <= Max?");

        // ================================================================
        // Max rounds exceeded — force needsHuman
        // ================================================================
        var forceNeedsHuman = new SetVariable
        {
            Id = "ForceNeedsHuman", Name = "Force Needs Human",
            Variable = decision,
            Value = new Input<object?>(ctx =>
            {
                reviewNotes.Set(ctx, $"Max review rounds ({maxRounds.Get(ctx)}) exceeded without consensus. Escalating to human.");
                return (object)"needsHuman";
            })
        };
        forceNeedsHuman.SetDisplayText("Force Needs Human");

        // ================================================================
        // Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                new SetOutput
                    { Id = "OutDecision", OutputName = new("decision"), OutputValue = new(ctx => (object)decision.Get(ctx)) },
                new SetOutput
                    { Id = "OutPlanJson", OutputName = new("planJson"), OutputValue = new(ctx => (object)planJson.Get(ctx)) },
                new SetOutput
                    { Id = "OutReviewNotes", OutputName = new("reviewNotes"), OutputValue = new(ctx => (object)reviewNotes.Get(ctx)) },
                new SetOutput
                    { Id = "OutDeferred", OutputName = new("deferred"), OutputValue = new(ctx => (object)deferred.Get(ctx)) },
                new SetOutput
                    { Id = "OutSplit", OutputName = new("split"), OutputValue = new(ctx => (object)split.Get(ctx)) },
                new SetOutput
                    { Id = "OutDiscussionLog", OutputName = new("discussionLog"), OutputValue = new(ctx => (object)discussionLog.Get(ctx)) },
                new SetOutput
                    { Id = "OutSuggestionsJson", OutputName = new("suggestionsJson"), OutputValue = new(ctx => (object)suggestionsJson.Get(ctx)) },
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "PlanReviewFlowchart",
            Start = init,
            Activities =
            {
                // Init
                init,

                // Phase 1: 7 role reviews (sequential) with per-role persistence
                phase1ArchCall, extractPhase1Arch, storePhase1Arch,
                phase1DevCall, extractPhase1Dev, storePhase1Dev,
                phase1TesterCall, extractPhase1Tester, storePhase1Tester,
                phase1SecCall, extractPhase1Sec, storePhase1Sec,
                phase1DevOpsCall, extractPhase1DevOps, storePhase1DevOps,
                phase1POCall, extractPhase1PO, storePhase1PO,
                phase1SrDevCall, extractPhase1SrDev, storePhase1SrDev,

                // Aggregate + anonymize
                aggregateReviews, buildAnonymized,

                // Phase 2: 7 rebuttals (sequential) with per-role persistence
                phase2ArchCall, extractPhase2Arch, storePhase2Arch,
                phase2DevCall, extractPhase2Dev, storePhase2Dev,
                phase2TesterCall, extractPhase2Tester, storePhase2Tester,
                phase2SecCall, extractPhase2Sec, storePhase2Sec,
                phase2DevOpsCall, extractPhase2DevOps, storePhase2DevOps,
                phase2POCall, extractPhase2PO, storePhase2PO,
                phase2SrDevCall, extractPhase2SrDev, storePhase2SrDev,

                // Aggregate rebuttals + early termination check
                aggregateRebuttals, earlyTermination,

                // Early termination path
                setApprovedEarly,

                // Phase 3: PO Decision
                phase3PODecisionCall, extractPODecision, storePODecisionActivity,
                poApprovedCheck, poNeedsHumanCheck,

                // Round management
                incrementRound, canContinue, forceNeedsHuman,

                // Outputs
                setOutputs, finish,
            },
            Connections =
            {
                // Init → Phase 1 sequential role reviews with per-role persistence
                Connect(init, phase1ArchCall),
                Connect(phase1ArchCall, extractPhase1Arch),
                Connect(extractPhase1Arch, storePhase1Arch),
                Connect(storePhase1Arch, phase1DevCall),

                Connect(phase1DevCall, extractPhase1Dev),
                Connect(extractPhase1Dev, storePhase1Dev),
                Connect(storePhase1Dev, phase1TesterCall),

                Connect(phase1TesterCall, extractPhase1Tester),
                Connect(extractPhase1Tester, storePhase1Tester),
                Connect(storePhase1Tester, phase1SecCall),

                Connect(phase1SecCall, extractPhase1Sec),
                Connect(extractPhase1Sec, storePhase1Sec),
                Connect(storePhase1Sec, phase1DevOpsCall),

                Connect(phase1DevOpsCall, extractPhase1DevOps),
                Connect(extractPhase1DevOps, storePhase1DevOps),
                Connect(storePhase1DevOps, phase1POCall),

                Connect(phase1POCall, extractPhase1PO),
                Connect(extractPhase1PO, storePhase1PO),
                Connect(storePhase1PO, phase1SrDevCall),

                Connect(phase1SrDevCall, extractPhase1SrDev),
                Connect(extractPhase1SrDev, storePhase1SrDev),

                // → Aggregate reviews → anonymize → Phase 2
                Connect(storePhase1SrDev, aggregateReviews),
                Connect(aggregateReviews, buildAnonymized),

                // Phase 2 sequential rebuttals
                Connect(buildAnonymized, phase2ArchCall),
                Connect(phase2ArchCall, extractPhase2Arch),
                Connect(extractPhase2Arch, storePhase2Arch),
                Connect(storePhase2Arch, phase2DevCall),

                Connect(phase2DevCall, extractPhase2Dev),
                Connect(extractPhase2Dev, storePhase2Dev),
                Connect(storePhase2Dev, phase2TesterCall),

                Connect(phase2TesterCall, extractPhase2Tester),
                Connect(extractPhase2Tester, storePhase2Tester),
                Connect(storePhase2Tester, phase2SecCall),

                Connect(phase2SecCall, extractPhase2Sec),
                Connect(extractPhase2Sec, storePhase2Sec),
                Connect(storePhase2Sec, phase2DevOpsCall),

                Connect(phase2DevOpsCall, extractPhase2DevOps),
                Connect(extractPhase2DevOps, storePhase2DevOps),
                Connect(storePhase2DevOps, phase2POCall),

                Connect(phase2POCall, extractPhase2PO),
                Connect(extractPhase2PO, storePhase2PO),
                Connect(storePhase2PO, phase2SrDevCall),

                Connect(phase2SrDevCall, extractPhase2SrDev),
                Connect(extractPhase2SrDev, storePhase2SrDev),

                // → Aggregate rebuttals → early termination check
                Connect(storePhase2SrDev, aggregateRebuttals),
                Connect(aggregateRebuttals, earlyTermination),

                // Early termination: all approve → set approved → outputs → finish
                ConnectOutcome(earlyTermination, "True", setApprovedEarly),
                Connect(setApprovedEarly, setOutputs),

                // Not all approve → Phase 3: PO Decision
                ConnectOutcome(earlyTermination, "False", phase3PODecisionCall),
                Connect(phase3PODecisionCall, extractPODecision),
                Connect(extractPODecision, storePODecisionActivity),
                Connect(storePODecisionActivity, poApprovedCheck),

                // PO approved → outputs → finish
                ConnectOutcome(poApprovedCheck, "True", setOutputs),

                // PO not approved → check needsHuman
                ConnectOutcome(poApprovedCheck, "False", poNeedsHumanCheck),

                // PO needsHuman → outputs → finish
                ConnectOutcome(poNeedsHumanCheck, "True", setOutputs),

                // PO needsModification → increment round → check max
                ConnectOutcome(poNeedsHumanCheck, "False", incrementRound),
                Connect(incrementRound, canContinue),

                // round <= max → loop back to Phase 2 (rebuild anonymized → rebuttals)
                ConnectOutcome(canContinue, "True", buildAnonymized),

                // round > max → force needsHuman → outputs
                ConnectOutcome(canContinue, "False", forceNeedsHuman),
                Connect(forceNeedsHuman, setOutputs),

                // Outputs → finish
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helper: Create a DispatchWorkflow for a Phase 1 role review
    // ================================================================
    private static DispatchWorkflow RoleReviewDispatch(
        string id, string displayName, string role,
        Variable<string> repository, Variable<string> planJson,
        Variable<string> contextIds, Variable<string> workItemJson,
        Variable<string> allReviewsJson,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role,
                ["action"] = "plan-review",
                ["variables"] = new Dictionary<string, object>
                {
                    ["planJson"] = planJson.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["previousReviews"] = allReviewsJson.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(displayName);
        return dispatch;
    }

    // ================================================================
    // Helper: Create a DispatchWorkflow for a Phase 2 rebuttal
    // ================================================================
    private static DispatchWorkflow RebuttalDispatch(
        string id, string displayName, string role,
        Variable<string> repository, Variable<string> planJson,
        Variable<string> contextIds, Variable<string> anonymizedReviewsJson,
        Variable<string> previousReview, Variable<int> roundCount,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role,
                ["action"] = "plan-review",
                ["variables"] = new Dictionary<string, object>
                {
                    ["planJson"] = planJson.Get(ctx),
                    ["allReviews"] = anonymizedReviewsJson.Get(ctx),
                    ["phase"] = "rebuttal",
                    ["previousReview"] = previousReview.Get(ctx),
                    ["roundNumber"] = roundCount.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(displayName);
        return dispatch;
    }

    // ================================================================
    // Helper: Extract a role's review from llmResult into a variable (Phase 1)
    // ================================================================
    private static SetVariable ExtractReview(
        Variable<string> target,
        Variable<IDictionary<string, object>?> llmResult,
        string role,
        string id, string displayName)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = target,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "{}";

                    // Try to extract JSON from the response
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var jsonCandidate = output[jsonStart..(jsonEnd + 1)];
                        try
                        {
                            // Validate it's parseable JSON
                            JsonDocument.Parse(jsonCandidate);
                            return (object)jsonCandidate;
                        }
                        catch
                        {
                            // Not valid JSON — wrap as comments
                        }
                    }

                    // Fallback: wrap raw text as a concerns review
                    return (object)JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["verdict"] = "concerns",
                        ["comments"] = output,
                        ["suggestedChanges"] = "",
                    });
                }
                return (object)"{}";
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    // ================================================================
    // Helper: Extract a role's rebuttal from llmResult into a variable (Phase 2)
    // ================================================================
    private static SetVariable ExtractRebuttal(
        Variable<string> target,
        Variable<IDictionary<string, object>?> llmResult,
        string id, string displayName)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = target,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "{}";

                    // Try to extract JSON from the response
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var jsonCandidate = output[jsonStart..(jsonEnd + 1)];
                        try
                        {
                            JsonDocument.Parse(jsonCandidate);
                            return (object)jsonCandidate;
                        }
                        catch
                        {
                            // Not valid JSON — wrap with default structure
                        }
                    }

                    // Fallback: wrap raw text as a "concerns" rebuttal
                    return (object)JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["responses"] = Array.Empty<object>(),
                        ["revisedVerdict"] = "concerns",
                        ["rawText"] = output,
                    });
                }
                return (object)"{}";
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    // ================================================================
    // Helper: Store a role's review/rebuttal result immediately after extraction
    // ================================================================
    private static StoreRoleFindingActivity StoreReviewRole(
        string id, string name, string role,
        Variable<string> repository, Variable<int> issueNumber,
        Variable<string> reviewVar)
    {
        var store = new StoreRoleFindingActivity
        {
            Id = id, Name = name,
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Role = new Input<string>(role),
            FindingsJson = new Input<string>(ctx => reviewVar.Get(ctx)),
            ContextId = new Output<string>(new Variable<string>()),
        };
        store.SetDisplayText(name);
        return store;
    }

    // ================================================================
    // Connection helpers
    // ================================================================
    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
