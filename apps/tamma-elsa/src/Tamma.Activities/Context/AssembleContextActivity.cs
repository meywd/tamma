using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Context.Models;

namespace Tamma.Activities.Context;

/// <summary>
/// Assembles all gathered context data from the parallel fetch activities into a single
/// AssembledContext object. Calculates estimated sizes for each section and assigns
/// priority based on the context purpose.
/// </summary>
[Activity(
    "Tamma.Context",
    "Assemble Context",
    "Combine all gathered context sources into a unified structure with priority annotations",
    Kind = ActivityKind.Task
)]
public class AssembleContextActivity : CodeActivity<AssembledContext>
{
    private readonly ILogger<AssembleContextActivity>? _logger;

    /// <summary>Story metadata from FetchStoryMetadataActivity</summary>
    [Input(Description = "Story metadata result")]
    public Input<StoryMetadata?> StoryMetadata { get; set; } = default!;

    /// <summary>Recent commits from FetchRecentCommitsActivity</summary>
    [Input(Description = "Recent commits result")]
    public Input<RecentCommitsResult?> RecentCommits { get; set; } = default!;

    /// <summary>File contents from FetchFileContentsActivity</summary>
    [Input(Description = "File contents result")]
    public Input<FileContentsResult?> FileContents { get; set; } = default!;

    /// <summary>Test results from FetchTestResultsActivity</summary>
    [Input(Description = "Test results")]
    public Input<TestResultsData?> TestResults { get; set; } = default!;

    /// <summary>Session history from FetchSessionHistoryActivity</summary>
    [Input(Description = "Session history result")]
    public Input<SessionHistoryResult?> SessionHistory { get; set; } = default!;

    /// <summary>Similar patterns from FetchSimilarPatternsActivity</summary>
    [Input(Description = "Similar patterns result")]
    public Input<SimilarPatternsResult?> SimilarPatterns { get; set; } = default!;

    /// <summary>The purpose of this context gathering, used for priority assignment</summary>
    [Input(Description = "Context purpose (Diagnosis, Review, Assessment, Planning, Implementation)")]
    public Input<ContextPurpose> Purpose { get; set; } = new(ContextPurpose.Assessment);

    [JsonConstructor]
    public AssembleContextActivity()
    {
    }

    public AssembleContextActivity(ILogger<AssembleContextActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var storyMetadata = StoryMetadata.GetOrDefault(context);
        var recentCommits = RecentCommits.GetOrDefault(context);
        var fileContents = FileContents.GetOrDefault(context);
        var testResults = TestResults.GetOrDefault(context);
        var sessionHistory = SessionHistory.GetOrDefault(context);
        var similarPatterns = SimilarPatterns.GetOrDefault(context);
        var purpose = Purpose.Get(context);

        _logger?.LogInformation(
            "Assembling context for purpose {Purpose}", purpose);

        var sections = new List<ContextSection>();

        // Story metadata is always critical priority
        if (storyMetadata?.Success == true)
        {
            var size = EstimateSize(storyMetadata);
            sections.Add(new ContextSection
            {
                Name = "StoryMetadata",
                Priority = ContextSourcePriority.Critical,
                EstimatedSize = size,
                Data = storyMetadata
            });
        }

        // Recent commits priority varies by purpose
        if (recentCommits?.Success == true && recentCommits.Commits.Any())
        {
            var size = EstimateSize(recentCommits);
            sections.Add(new ContextSection
            {
                Name = "RecentCommits",
                Priority = GetCommitsPriority(purpose),
                EstimatedSize = size,
                Data = recentCommits
            });
        }

        // File contents priority varies by purpose
        if (fileContents?.Success == true && fileContents.Files.Any())
        {
            var size = fileContents.TotalSize;
            sections.Add(new ContextSection
            {
                Name = "FileContents",
                Priority = GetFileContentsPriority(purpose),
                EstimatedSize = size,
                Data = fileContents
            });
        }

        // Test results are high priority for Diagnosis and Review
        if (testResults?.Success == true)
        {
            var size = EstimateSize(testResults);
            sections.Add(new ContextSection
            {
                Name = "TestResults",
                Priority = GetTestResultsPriority(purpose),
                EstimatedSize = size,
                Data = testResults
            });
        }

        // Session history is medium priority for most purposes
        if (sessionHistory?.Success == true && sessionHistory.Events.Any())
        {
            var size = EstimateSize(sessionHistory);
            sections.Add(new ContextSection
            {
                Name = "SessionHistory",
                Priority = GetSessionHistoryPriority(purpose),
                EstimatedSize = size,
                Data = sessionHistory
            });
        }

        // Similar patterns are lowest priority and trimmed first
        if (similarPatterns?.Success == true && similarPatterns.Patterns.Any())
        {
            var size = EstimateSize(similarPatterns);
            sections.Add(new ContextSection
            {
                Name = "SimilarPatterns",
                Priority = ContextSourcePriority.Low,
                EstimatedSize = size,
                Data = similarPatterns
            });
        }

        var totalSize = sections.Sum(s => s.EstimatedSize);

        var assembled = new AssembledContext
        {
            StoryMetadata = storyMetadata,
            RecentCommits = recentCommits,
            FileContents = fileContents,
            TestResults = testResults,
            SessionHistory = sessionHistory,
            SimilarPatterns = similarPatterns,
            Purpose = purpose,
            TotalEstimatedSize = totalSize,
            Sections = sections
        };

        _logger?.LogInformation(
            "Context assembled: {SectionCount} sections, {TotalSize} chars estimated",
            sections.Count, totalSize);

        context.SetResult(assembled);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Priority for commits depends on purpose:
    /// - Diagnosis and Review need to see recent changes (High)
    /// - Implementation benefits from commits (Medium)
    /// - Assessment and Planning less so (Low)
    /// </summary>
    private static ContextSourcePriority GetCommitsPriority(ContextPurpose purpose)
    {
        return purpose switch
        {
            ContextPurpose.Diagnosis => ContextSourcePriority.High,
            ContextPurpose.Review => ContextSourcePriority.High,
            ContextPurpose.Implementation => ContextSourcePriority.Medium,
            _ => ContextSourcePriority.Low
        };
    }

    /// <summary>
    /// File contents are high priority for implementation and review.
    /// </summary>
    private static ContextSourcePriority GetFileContentsPriority(ContextPurpose purpose)
    {
        return purpose switch
        {
            ContextPurpose.Implementation => ContextSourcePriority.High,
            ContextPurpose.Review => ContextSourcePriority.High,
            ContextPurpose.Diagnosis => ContextSourcePriority.Medium,
            _ => ContextSourcePriority.Medium
        };
    }

    /// <summary>
    /// Test results are critical for diagnosis, high for review.
    /// </summary>
    private static ContextSourcePriority GetTestResultsPriority(ContextPurpose purpose)
    {
        return purpose switch
        {
            ContextPurpose.Diagnosis => ContextSourcePriority.Critical,
            ContextPurpose.Review => ContextSourcePriority.High,
            _ => ContextSourcePriority.Medium
        };
    }

    /// <summary>
    /// Session history is most valuable for diagnosis and assessment.
    /// </summary>
    private static ContextSourcePriority GetSessionHistoryPriority(ContextPurpose purpose)
    {
        return purpose switch
        {
            ContextPurpose.Diagnosis => ContextSourcePriority.High,
            ContextPurpose.Assessment => ContextSourcePriority.High,
            _ => ContextSourcePriority.Medium
        };
    }

    private static int EstimateSize(object obj)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj);
            return json.Length;
        }
        catch
        {
            return 500; // Fallback estimate
        }
    }
}
