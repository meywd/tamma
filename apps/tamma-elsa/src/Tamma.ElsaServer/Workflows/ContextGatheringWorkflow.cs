using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.Context;
using Tamma.Activities.Context.Models;
using ElsaParallel = Elsa.Workflows.Activities.Parallel;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Context Gathering sub-workflow.
///
/// Collects contextual data from multiple sources in parallel using ELSA's Parallel
/// activity (fan-out/fan-in). Each source fetch is a separate ELSA activity
/// (visible, auditable in ELSA Studio). After all parallel fetches complete, the workflow
/// assembles the gathered data and applies priority-based budget trimming.
///
/// Design: Flowchart with visible nodes for each phase in ELSA Studio.
///
/// Flow:
///   InitInputs → IndependentFetches → StoryMetadataOk?
///     No  → FaultNode (abort)
///     Yes → TrackPhase1 → DependentFetches → TrackPhase2
///           → AssembleContext → ApplyBudget → SetOutputs
/// </summary>
public class ContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Context Gathering Sub-Workflow";
        builder.DefinitionId = "context-gathering";
        builder.Description =
            "Gathers context from multiple sources in parallel, assembles it, and applies budget trimming.";

        // ============================================
        // Workflow variables (set from parent workflow input)
        // ============================================
        var sessionId = builder.WithVariable<Guid>()
            .WithWorkflowStorage();
        var storyId = builder.WithVariable<string>()
            .WithWorkflowStorage();
        var repositoryUrl = builder.WithVariable<string>()
            .WithWorkflowStorage();
        var targetFiles = builder.WithVariable<List<string>?>()
            .WithWorkflowStorage();
        var maxContextSize = builder.WithVariable<int>()
            .WithWorkflowStorage();
        var purpose = builder.WithVariable<ContextPurpose>()
            .WithWorkflowStorage();

        // Variables to hold intermediate results from parallel branches
        var storyMetadataResult = builder.WithVariable<StoryMetadata?>()
            .WithWorkflowStorage();
        var recentCommitsResult = builder.WithVariable<RecentCommitsResult?>()
            .WithWorkflowStorage();
        var fileContentsResult = builder.WithVariable<FileContentsResult?>()
            .WithWorkflowStorage();
        var testResultsResult = builder.WithVariable<TestResultsData?>()
            .WithWorkflowStorage();
        var sessionHistoryResult = builder.WithVariable<SessionHistoryResult?>()
            .WithWorkflowStorage();
        var similarPatternsResult = builder.WithVariable<SimilarPatternsResult?>()
            .WithWorkflowStorage();
        var assembledResult = builder.WithVariable<AssembledContext>()
            .WithWorkflowStorage();
        var budgetResult = builder.WithVariable<ContextGatheringOutput?>()
            .WithWorkflowStorage();

        // Tracking variables
        var failedSources = builder.WithVariable<string>()
            .WithWorkflowStorage();
        failedSources.Value = "[]";
        var contextSuccess = builder.WithVariable<bool>()
            .WithWorkflowStorage();
        contextSuccess.Value = true;

        // ============================================
        // Activities
        // ============================================

        // 1. Input initialization: set variables from workflow input
        var initInputs = new Sequence
        {
            Id = "InitInputs",
            Name = "Initialize Inputs",
            Activities =
            {
                WithLabel(new SetVariable
                {
                    Id = "SetSessionId",
                    Name = "Set SessionId",
                    Variable = sessionId,
                    Value = new(ctx => ctx.GetInput<Guid>("SessionId"))
                }, "Set SessionId"),
                WithLabel(new SetVariable
                {
                    Id = "SetStoryId",
                    Name = "Set StoryId",
                    Variable = storyId,
                    Value = new(ctx => ctx.GetInput<string>("StoryId") ?? string.Empty)
                }, "Set StoryId"),
                WithLabel(new SetVariable
                {
                    Id = "SetRepositoryUrl",
                    Name = "Set RepositoryUrl",
                    Variable = repositoryUrl,
                    Value = new(ctx => ctx.GetInput<string>("RepositoryUrl") ?? string.Empty)
                }, "Set RepositoryUrl"),
                WithLabel(new SetVariable
                {
                    Id = "SetTargetFiles",
                    Name = "Set TargetFiles",
                    Variable = targetFiles,
                    Value = new(ctx => ctx.GetInput<List<string>?>("TargetFiles"))
                }, "Set TargetFiles"),
                WithLabel(new SetVariable
                {
                    Id = "SetMaxContextSize",
                    Name = "Set MaxContextSize",
                    Variable = maxContextSize,
                    Value = new(ctx => ctx.GetInput<int?>("MaxContextSize") ?? 50000)
                }, "Set MaxContextSize"),
                WithLabel(new SetVariable
                {
                    Id = "SetPurpose",
                    Name = "Set Purpose",
                    Variable = purpose,
                    Value = new(ctx =>
                        ctx.GetInput<ContextPurpose?>("Purpose") ?? ContextPurpose.Assessment)
                }, "Set Purpose")
            }
        };
        initInputs.SetDisplayText("Initialize Inputs");

        // 2. Phase 1: Independent parallel fetches
        var independentFetches = new ElsaParallel
        {
            Id = "IndependentFetches",
            Name = "Phase 1: Parallel Fetches",
            Activities =
            {
                WithLabel(new FetchStoryMetadataActivity
                {
                    Id = "FetchStoryMetadata",
                    Name = "Fetch Story Metadata",
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    Result = new(storyMetadataResult)
                }, "Fetch Story Metadata"),
                WithLabel(new FetchRecentCommitsActivity
                {
                    Id = "FetchRecentCommits",
                    Name = "Fetch Recent Commits",
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    Result = new(recentCommitsResult)
                }, "Fetch Recent Commits"),
                WithLabel(new FetchTestResultsActivity
                {
                    Id = "FetchTestResults",
                    Name = "Fetch Test Results",
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    Result = new(testResultsResult)
                }, "Fetch Test Results"),
                WithLabel(new FetchSessionHistoryActivity
                {
                    Id = "FetchSessionHistory",
                    Name = "Fetch Session History",
                    SessionId = new(ctx => sessionId.Get(ctx)),
                    Result = new(sessionHistoryResult)
                }, "Fetch Session History")
            }
        };
        independentFetches.SetDisplayText("Phase 1: Parallel Fetches");

        // 3. Viability check: Story metadata is critical
        var storyMetadataOk = new FlowDecision(ctx => storyMetadataResult.Get(ctx) != null)
        {
            Id = "StoryMetadataOk",
            Name = "Story Metadata OK?"
        };
        storyMetadataOk.SetDisplayText("Story Metadata OK?");

        // 3a. Fault if story metadata missing
        var faultNode = new Sequence
        {
            Id = "FaultNoMetadata",
            Name = "Fault (No Metadata)",
            Activities =
            {
                WithLabel(new SetVariable
                {
                    Id = "SetContextFailed",
                    Name = "Set Context Failed",
                    Variable = contextSuccess,
                    Value = new(false)
                }, "Set Context Failed"),
                new Fault("Story metadata fetch failed completely — context gathering cannot proceed without story metadata.")
            }
        };
        faultNode.SetDisplayText("Fault (No Metadata)");

        // 4. Track Phase 1 failures
        var trackPhase1Failures = new SetVariable
        {
            Id = "TrackPhase1Failures",
            Name = "Track Phase 1 Failures",
            Variable = failedSources,
            Value = new(ctx =>
            {
                var current = DeserializeStringList(failedSources.Get(ctx));
                var updated = new List<string>(current);
                if (storyMetadataResult.Get(ctx) == null)
                    updated.Add("StoryMetadata");
                if (recentCommitsResult.Get(ctx) == null)
                    updated.Add("RecentCommits");
                if (testResultsResult.Get(ctx) == null)
                    updated.Add("TestResults");
                if (sessionHistoryResult.Get(ctx) == null)
                    updated.Add("SessionHistory");
                return JsonSerializer.Serialize(updated);
            })
        };
        trackPhase1Failures.SetDisplayText("Track Phase 1 Failures");

        // 5. Phase 2: Dependent parallel fetches
        var dependentFetches = new ElsaParallel
        {
            Id = "DependentFetches",
            Name = "Phase 2: Dependent Fetches",
            Activities =
            {
                WithLabel(new FetchFileContentsActivity
                {
                    Id = "FetchFileContents",
                    Name = "Fetch File Contents",
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    TargetFiles = new(ctx => targetFiles.Get(ctx)),
                    StoryDescription = new(ctx => storyMetadataResult.Get(ctx)?.Description),
                    CommitFiles = new(ctx =>
                    {
                        var commits = recentCommitsResult.Get(ctx);
                        if (commits?.Commits == null || !commits.Commits.Any())
                            return null;
                        return commits.Commits
                            .SelectMany(c => c.Files)
                            .Distinct()
                            .ToList();
                    }),
                    Result = new(fileContentsResult)
                }, "Fetch File Contents"),
                WithLabel(new FetchSimilarPatternsActivity
                {
                    Id = "FetchSimilarPatterns",
                    Name = "Fetch Similar Patterns",
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryTitle = new(ctx =>
                        storyMetadataResult.Get(ctx)?.Title ?? string.Empty),
                    StoryTags = new(ctx => storyMetadataResult.Get(ctx)?.Tags),
                    Result = new(similarPatternsResult)
                }, "Fetch Similar Patterns")
            }
        };
        dependentFetches.SetDisplayText("Phase 2: Dependent Fetches");

        // 6. Track Phase 2 failures
        var trackPhase2Failures = new SetVariable
        {
            Id = "TrackPhase2Failures",
            Name = "Track Phase 2 Failures",
            Variable = failedSources,
            Value = new(ctx =>
            {
                var current = DeserializeStringList(failedSources.Get(ctx));
                var updated = new List<string>(current);
                if (fileContentsResult.Get(ctx) == null)
                    updated.Add("FileContents");
                if (similarPatternsResult.Get(ctx) == null)
                    updated.Add("SimilarPatterns");
                return JsonSerializer.Serialize(updated);
            })
        };
        trackPhase2Failures.SetDisplayText("Track Phase 2 Failures");

        // 7. Assemble context
        var assembleContext = new AssembleContextActivity
        {
            Id = "AssembleContext",
            Name = "Assemble Context",
            StoryMetadata = new(ctx => storyMetadataResult.Get(ctx)),
            RecentCommits = new(ctx => recentCommitsResult.Get(ctx)),
            FileContents = new(ctx => fileContentsResult.Get(ctx)),
            TestResults = new(ctx => testResultsResult.Get(ctx)),
            SessionHistory = new(ctx => sessionHistoryResult.Get(ctx)),
            SimilarPatterns = new(ctx => similarPatternsResult.Get(ctx)),
            Purpose = new(ctx => purpose.Get(ctx)),
            Result = new(assembledResult)
        };
        assembleContext.SetDisplayText("Assemble Context");

        // 8. Apply budget
        var applyBudget = new ApplyBudgetActivity
        {
            Id = "ApplyBudget",
            Name = "Apply Budget",
            AssembledContext = new(ctx => assembledResult.Get(ctx)!),
            MaxContextSize = new(ctx => maxContextSize.Get(ctx)),
            StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
            Result = new(budgetResult)
        };
        applyBudget.SetDisplayText("Apply Budget");

        // 9. Set workflow outputs
        var setOutputs = new Sequence
        {
            Id = "SetOutputs",
            Name = "Set Outputs",
            Activities =
            {
                WithLabel(new SetOutput
                {
                    Id = "OutputContextJson",
                    Name = "Output contextJson",
                    OutputName = new("contextJson"),
                    OutputValue = new(ctx =>
                    {
                        var result = budgetResult.Get(ctx);
                        return (object)(result != null ? JsonSerializer.Serialize(result) : "{}");
                    })
                }, "Output contextJson"),
                WithLabel(new SetOutput
                {
                    Id = "OutputSuccess",
                    Name = "Output success",
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)contextSuccess.Get(ctx))
                }, "Output success"),
                WithLabel(new SetOutput
                {
                    Id = "OutputFailedSources",
                    Name = "Output failedSources",
                    OutputName = new("failedSources"),
                    OutputValue = new(ctx => (object)(failedSources.Get(ctx) ?? "[]"))
                }, "Output failedSources")
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        // ============================================
        // Flowchart
        // ============================================
        builder.Root = new Flowchart
        {
            Id = "ContextGatheringFlowchart",
            Start = initInputs,
            Activities =
            {
                initInputs, independentFetches, storyMetadataOk, faultNode,
                trackPhase1Failures, dependentFetches, trackPhase2Failures,
                assembleContext, applyBudget, setOutputs
            },
            Connections =
            {
                // InitInputs → Phase 1: Parallel Fetches
                Connect(initInputs, independentFetches),

                // Phase 1 → Story Metadata OK?
                Connect(independentFetches, storyMetadataOk),

                // Story Metadata OK? Yes → Track Phase 1 Failures
                ConnectOutcome(storyMetadataOk, "True", trackPhase1Failures),

                // Story Metadata OK? No → Fault
                ConnectOutcome(storyMetadataOk, "False", faultNode),

                // Track Phase 1 → Phase 2: Dependent Fetches
                Connect(trackPhase1Failures, dependentFetches),

                // Phase 2 → Track Phase 2 Failures
                Connect(dependentFetches, trackPhase2Failures),

                // Track Phase 2 → Assemble Context
                Connect(trackPhase2Failures, assembleContext),

                // Assemble → Apply Budget
                Connect(assembleContext, applyBudget),

                // Apply Budget → Set Outputs
                Connect(applyBudget, setOutputs)
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

    // ================================================================
    // Helper methods (static, used in expression lambdas)
    // ================================================================

    private static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();

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
