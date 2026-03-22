using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Context.Models;

namespace Tamma.Activities.Context;

/// <summary>
/// Applies priority-based budget trimming to the assembled context.
/// Sections are trimmed from lowest priority first. Within a priority level,
/// sections are trimmed by estimated size (largest first) to maximize the
/// number of sources retained.
///
/// Trimming strategy:
///   1. Remove entire sections from lowest priority up until within budget
///   2. If still over budget after removing all Optional/Low sections,
///      trim file contents within medium-priority sections (fewest-relevance files first)
///   3. Critical sections are never fully removed (only truncated as last resort)
/// </summary>
[Activity(
    "Tamma.Context",
    "Apply Budget",
    "Apply priority-based budget trimming to keep context within the character limit",
    Kind = ActivityKind.Task
)]
public class ApplyBudgetActivity : CodeActivity<ContextGatheringOutput>
{
    private readonly ILogger<ApplyBudgetActivity>? _logger;

    /// <summary>Assembled context from AssembleContextActivity</summary>
    [Input(Description = "Assembled context to trim")]
    public Input<AssembledContext> AssembledContext { get; set; } = default!;

    /// <summary>Maximum context size in characters</summary>
    [Input(Description = "Maximum context size in characters", DefaultValue = 50000)]
    public Input<int> MaxContextSize { get; set; } = new(50000);

    /// <summary>Story ID for the output</summary>
    [Input(Description = "Story ID")]
    public Input<string> StoryId { get; set; } = default!;

    [JsonConstructor]
    public ApplyBudgetActivity()
    {
    }

    public ApplyBudgetActivity(ILogger<ApplyBudgetActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var assembled = AssembledContext.Get(context);
        var maxSize = MaxContextSize.Get(context);
        var storyId = StoryId.Get(context);

        _logger?.LogInformation(
            "Applying budget of {MaxSize} chars to context with {TotalSize} chars estimated",
            maxSize, assembled.TotalEstimatedSize);

        var sectionsTrimmed = 0;

        if (assembled.TotalEstimatedSize > maxSize)
        {
            sectionsTrimmed = TrimSections(assembled, maxSize);
        }

        // Build output
        var output = new ContextGatheringOutput
        {
            Success = true,
            StoryId = storyId,
            Purpose = assembled.Purpose,
            StoryMetadata = assembled.StoryMetadata,
            RecentCommits = assembled.RecentCommits,
            FileContents = assembled.FileContents,
            TestResults = assembled.TestResults,
            SessionHistory = assembled.SessionHistory,
            SimilarPatterns = assembled.SimilarPatterns,
            TotalContextSize = assembled.Sections
                .Where(s => !s.Trimmed)
                .Sum(s => s.EstimatedSize),
            BudgetLimit = maxSize,
            SectionsTrimmed = sectionsTrimmed,
            ContextSummary = GenerateSummary(assembled)
        };

        // Null out trimmed sections in the output
        foreach (var section in assembled.Sections.Where(s => s.Trimmed))
        {
            switch (section.Name)
            {
                case "SimilarPatterns":
                    output.SimilarPatterns = null;
                    break;
                case "SessionHistory":
                    output.SessionHistory = null;
                    break;
                case "TestResults":
                    output.TestResults = null;
                    break;
                case "RecentCommits":
                    output.RecentCommits = null;
                    break;
                case "FileContents":
                    output.FileContents = null;
                    break;
                // StoryMetadata is never fully trimmed
            }
        }

        _logger?.LogInformation(
            "Budget applied: {FinalSize}/{MaxSize} chars, {Trimmed} sections trimmed",
            output.TotalContextSize, maxSize, sectionsTrimmed);

        context.SetResult(output);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Trim sections from lowest priority first, largest first within a priority level.
    /// Returns the number of sections trimmed.
    /// </summary>
    private int TrimSections(AssembledContext assembled, int maxSize)
    {
        var trimmed = 0;

        // Sort sections: lowest priority first (highest numeric value), then largest first
        var trimOrder = assembled.Sections
            .Where(s => s.Priority != ContextSourcePriority.Critical)
            .OrderByDescending(s => (int)s.Priority)
            .ThenByDescending(s => s.EstimatedSize)
            .ToList();

        var currentSize = assembled.TotalEstimatedSize;

        // Phase 1: Remove entire sections from lowest priority
        foreach (var section in trimOrder)
        {
            if (currentSize <= maxSize)
                break;

            section.Trimmed = true;
            currentSize -= section.EstimatedSize;
            trimmed++;

            _logger?.LogDebug(
                "Trimmed section '{Name}' (priority={Priority}, size={Size}), remaining={Remaining}",
                section.Name, section.Priority, section.EstimatedSize, currentSize);
        }

        // Phase 2: If still over budget, trim file contents within remaining sections
        if (currentSize > maxSize && assembled.FileContents?.Files.Any() == true)
        {
            var fileSection = assembled.Sections
                .FirstOrDefault(s => s.Name == "FileContents" && !s.Trimmed);

            if (fileSection != null)
            {
                var files = assembled.FileContents.Files;
                // Remove files with lowest relevance first
                var sortedFiles = files.OrderBy(f => f.RelevanceScore).ToList();

                while (currentSize > maxSize && sortedFiles.Count > 1)
                {
                    var removed = sortedFiles[0];
                    sortedFiles.RemoveAt(0);
                    var removedSize = removed.Content?.Length ?? 0;
                    currentSize -= removedSize;

                    _logger?.LogDebug(
                        "Trimmed file '{File}' (relevance={Relevance}, size={Size})",
                        removed.FilePath, removed.RelevanceScore, removedSize);
                }

                assembled.FileContents.Files = sortedFiles;
                assembled.FileContents.TotalFiles = sortedFiles.Count;
                assembled.FileContents.TotalSize = sortedFiles.Sum(f => f.Content?.Length ?? 0);

                // Update section size estimate
                fileSection.EstimatedSize = assembled.FileContents.TotalSize;
            }
        }

        // Phase 3: If still over budget after all trimming, truncate story description
        if (currentSize > maxSize && assembled.StoryMetadata?.Description != null)
        {
            var storySection = assembled.Sections
                .FirstOrDefault(s => s.Name == "StoryMetadata");
            if (storySection != null)
            {
                var desc = assembled.StoryMetadata.Description;
                var excess = currentSize - maxSize;
                if (desc.Length > excess + 100) // Keep at least 100 chars
                {
                    assembled.StoryMetadata.Description =
                        desc[..(desc.Length - excess)] + "... [truncated]";
                }
            }
        }

        return trimmed;
    }

    private static string GenerateSummary(AssembledContext assembled)
    {
        var parts = new List<string>();

        if (assembled.StoryMetadata?.Success == true)
            parts.Add($"Story: {assembled.StoryMetadata.Title}");

        var activeSections = assembled.Sections.Where(s => !s.Trimmed).ToList();

        if (assembled.FileContents?.Success == true)
            parts.Add($"Files: {assembled.FileContents.TotalFiles}");

        if (assembled.RecentCommits?.Success == true)
            parts.Add($"Commits: {assembled.RecentCommits.TotalCommits}");

        if (assembled.TestResults?.Success == true)
            parts.Add($"Tests: {assembled.TestResults.PassingTests}/{assembled.TestResults.TotalTests} passing");

        if (assembled.SessionHistory?.Success == true)
            parts.Add($"Events: {assembled.SessionHistory.TotalEvents}");

        if (assembled.SimilarPatterns?.Success == true)
            parts.Add($"Patterns: {assembled.SimilarPatterns.Patterns.Count}");

        parts.Add($"Purpose: {assembled.Purpose}");

        var trimmedCount = assembled.Sections.Count(s => s.Trimmed);
        if (trimmedCount > 0)
            parts.Add($"Trimmed: {trimmedCount} sections");

        return string.Join(" | ", parts);
    }
}
