using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Context.Models;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Context;

/// <summary>
/// Fetches story metadata (title, description, acceptance criteria, technical requirements)
/// from the database. This is the highest-priority context source.
/// </summary>
[Activity(
    "Tamma.Context",
    "Fetch Story Metadata",
    "Retrieve story details including acceptance criteria and technical requirements",
    Kind = ActivityKind.Task
)]
public class FetchStoryMetadataActivity : CodeActivity<StoryMetadata>
{
    private readonly ILogger<FetchStoryMetadataActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>ID of the story to fetch metadata for</summary>
    [Input(Description = "ID of the story")]
    public Input<string> StoryId { get; set; } = default!;

    [JsonConstructor]
    public FetchStoryMetadataActivity()
    {
    }

    public FetchStoryMetadataActivity(
        ILogger<FetchStoryMetadataActivity> logger,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var storyId = StoryId.Get(context);

        _logger?.LogInformation("Fetching story metadata for {StoryId}", storyId);

        try
        {
            var story = await _repository!.GetStoryByIdAsync(storyId);

            if (story == null)
            {
                _logger?.LogWarning("Story {StoryId} not found", storyId);
                context.SetResult(new StoryMetadata
                {
                    StoryId = storyId,
                    Success = false,
                    ErrorMessage = $"Story {storyId} not found"
                });
                return;
            }

            var acceptanceCriteria = ParseAcceptanceCriteria(
                story.AcceptanceCriteria?.RootElement.GetRawText());

            var technicalRequirements = ParseTechnicalRequirements(
                story.TechnicalRequirements?.RootElement.GetRawText());

            context.SetResult(new StoryMetadata
            {
                StoryId = storyId,
                Title = story.Title,
                Description = story.Description,
                AcceptanceCriteria = acceptanceCriteria,
                TechnicalRequirements = technicalRequirements,
                RepositoryUrl = story.RepositoryUrl,
                Priority = story.Priority,
                Complexity = story.Complexity,
                Tags = story.Tags,
                Success = true
            });

            _logger?.LogInformation(
                "Story metadata fetched for {StoryId}: {Title}",
                storyId, story.Title);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error fetching story metadata for {StoryId}", storyId);
            context.SetResult(new StoryMetadata
            {
                StoryId = storyId,
                Success = false,
                ErrorMessage = $"Failed to fetch story metadata: {ex.Message}"
            });
        }
    }

    private static List<string> ParseAcceptanceCriteria(string? criteria)
    {
        if (string.IsNullOrEmpty(criteria))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(criteria) ?? new List<string>();
        }
        catch
        {
            return new List<string> { criteria };
        }
    }

    private static Dictionary<string, string> ParseTechnicalRequirements(string? requirements)
    {
        if (string.IsNullOrEmpty(requirements))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(requirements)
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string> { { "raw", requirements } };
        }
    }
}
