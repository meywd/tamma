using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Tamma.Activities.Context;
using Tamma.Activities.Context.Models;
using ElsaParallel = Elsa.Workflows.Activities.Parallel;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Context Gathering sub-workflow.
///
/// Collects contextual data from multiple sources in parallel using ELSA's Parallel
/// activity (fan-out/fan-in). Each source fetch is a separate ELSA activity
/// (visible, auditable in ELSA Studio). After all parallel fetches complete, the workflow
/// assembles the gathered data and applies priority-based budget trimming.
///
/// Phase 1 (parallel, independent):
///   1. FetchStoryMetadata
///   2. FetchRecentCommits
///   3. FetchTestResults
///   4. FetchSessionHistory
///
/// Phase 2 (parallel, depends on Phase 1 results):
///   5. FetchFileContents (uses commit files for relevance scoring)
///   6. FetchSimilarPatterns (uses story title and tags)
///
/// Phase 3 (sequential):
///   7. AssembleContext (merges all results with priority annotations)
///   8. ApplyBudget (trims to character budget, lowest priority first)
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
        // Input initialization: set variables from workflow input
        // ============================================
        var initInputs = new Sequence
        {
            Activities =
            {
                new SetVariable
                {
                    Variable = sessionId,
                    Value = new(ctx => ctx.GetInput<Guid>("SessionId"))
                },
                new SetVariable
                {
                    Variable = storyId,
                    Value = new(ctx => ctx.GetInput<string>("StoryId") ?? string.Empty)
                },
                new SetVariable
                {
                    Variable = repositoryUrl,
                    Value = new(ctx => ctx.GetInput<string>("RepositoryUrl") ?? string.Empty)
                },
                new SetVariable
                {
                    Variable = targetFiles,
                    Value = new(ctx => ctx.GetInput<List<string>?>("TargetFiles"))
                },
                new SetVariable
                {
                    Variable = maxContextSize,
                    Value = new(ctx => ctx.GetInput<int?>("MaxContextSize") ?? 50000)
                },
                new SetVariable
                {
                    Variable = purpose,
                    Value = new(ctx =>
                        ctx.GetInput<ContextPurpose?>("Purpose") ?? ContextPurpose.Assessment)
                }
            }
        };

        // ============================================
        // Phase 1: Independent parallel fetches
        // Story metadata, recent commits, test results, and session history
        // can all run independently without depending on each other.
        // ============================================
        var independentFetches = new ElsaParallel
        {
            Activities =
            {
                new FetchStoryMetadataActivity
                {
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    Result = new(storyMetadataResult)
                },
                new FetchRecentCommitsActivity
                {
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    Result = new(recentCommitsResult)
                },
                new FetchTestResultsActivity
                {
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
                    Result = new(testResultsResult)
                },
                new FetchSessionHistoryActivity
                {
                    SessionId = new(ctx => sessionId.Get(ctx)),
                    Result = new(sessionHistoryResult)
                }
            }
        };

        // ============================================
        // Minimum viability check: Story metadata is the most critical source.
        // If it fails completely, the remaining context is insufficient.
        // ============================================
        var storyMetadataCheck = new If
        {
            Condition = new(ctx => storyMetadataResult.Get(ctx) == null),
            Then = new Sequence
            {
                Activities =
                {
                    new SetVariable
                    {
                        Variable = contextSuccess,
                        Value = new(false)
                    },
                    new Fault("Story metadata fetch failed completely — context gathering cannot proceed without story metadata.")
                }
            }
        };

        // ============================================
        // Phase 1 failed sources tracking (immutable list pattern)
        // ============================================
        var trackPhase1Failures = new SetVariable
        {
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

        // ============================================
        // Phase 2: Dependent parallel fetches
        // File contents needs commit file list for relevance scoring.
        // Similar patterns needs story title and tags.
        // These run in parallel with each other but after Phase 1.
        // ============================================
        var dependentFetches = new ElsaParallel
        {
            Activities =
            {
                new FetchFileContentsActivity
                {
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
                },
                new FetchSimilarPatternsActivity
                {
                    RepositoryUrl = new(ctx => repositoryUrl.Get(ctx) ?? string.Empty),
                    StoryTitle = new(ctx =>
                        storyMetadataResult.Get(ctx)?.Title ?? string.Empty),
                    StoryTags = new(ctx => storyMetadataResult.Get(ctx)?.Tags),
                    Result = new(similarPatternsResult)
                }
            }
        };

        // ============================================
        // Phase 2 failed sources tracking (immutable list pattern)
        // ============================================
        var trackPhase2Failures = new SetVariable
        {
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

        // ============================================
        // Phase 3: Assemble all results and apply budget
        // ============================================
        var assembleContext = new AssembleContextActivity
        {
            StoryMetadata = new(ctx => storyMetadataResult.Get(ctx)),
            RecentCommits = new(ctx => recentCommitsResult.Get(ctx)),
            FileContents = new(ctx => fileContentsResult.Get(ctx)),
            TestResults = new(ctx => testResultsResult.Get(ctx)),
            SessionHistory = new(ctx => sessionHistoryResult.Get(ctx)),
            SimilarPatterns = new(ctx => similarPatternsResult.Get(ctx)),
            Purpose = new(ctx => purpose.Get(ctx)),
            Result = new(assembledResult)
        };

        var applyBudget = new ApplyBudgetActivity
        {
            AssembledContext = new(ctx => assembledResult.Get(ctx)!),
            MaxContextSize = new(ctx => maxContextSize.Get(ctx)),
            StoryId = new(ctx => storyId.Get(ctx) ?? string.Empty),
            Result = new(budgetResult)
        };

        // ============================================
        // Workflow structure: Sequential phases
        // Phase 1 (parallel) -> Viability Check -> Phase 2 (parallel) -> Assemble -> Budget -> SetOutputs
        // ============================================
        builder.Root = new Sequence
        {
            Activities =
            {
                initInputs,
                independentFetches,
                storyMetadataCheck,
                trackPhase1Failures,
                dependentFetches,
                trackPhase2Failures,
                assembleContext,
                applyBudget,

                // ── Set workflow outputs for parent consumption ──
                new SetOutput
                {
                    OutputName = new("contextJson"),
                    OutputValue = new(ctx =>
                    {
                        var result = budgetResult.Get(ctx);
                        return (object)(result != null ? JsonSerializer.Serialize(result) : "{}");
                    })
                },
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)contextSuccess.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("failedSources"),
                    OutputValue = new(ctx => (object)(failedSources.Get(ctx) ?? "[]"))
                }
            }
        };
    }

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
