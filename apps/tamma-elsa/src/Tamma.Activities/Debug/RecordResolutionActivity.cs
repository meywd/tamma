using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Debug;

/// <summary>
/// Records debug resolution data when a fix succeeds.
/// Stores root cause category, fix approach, files involved, and timing data
/// for pattern analysis and future reference.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Record Resolution",
    "Store resolution data for future reference and pattern analysis",
    Kind = ActivityKind.Task
)]
public class RecordResolutionActivity : CodeActivity
{
    private readonly ILogger<RecordResolutionActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>Session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    /// <summary>Root cause description</summary>
    [Input(Description = "Root cause description")]
    public Input<string> RootCause { get; set; } = default!;

    /// <summary>Fix approach description</summary>
    [Input(Description = "Fix approach that worked")]
    public Input<string> FixApproach { get; set; } = default!;

    /// <summary>Files changed by the fix (JSON array)</summary>
    [Input(Description = "Files changed (JSON array)")]
    public Input<string> FilesChangedJson { get; set; } = default!;

    /// <summary>Number of attempts to resolution</summary>
    [Input(Description = "Number of fix attempts")]
    public Input<int> Attempts { get; set; } = default!;

    /// <summary>Debug start time (ISO 8601)</summary>
    [Input(Description = "Debug session start time")]
    public Input<string> StartTime { get; set; } = default!;

    [JsonConstructor]
    public RecordResolutionActivity() { }

    public RecordResolutionActivity(
        ILogger<RecordResolutionActivity> logger,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context) ?? "unknown";
        var mode = DebugContextMode.Get(context) ?? "unknown";
        var rootCause = RootCause.Get(context) ?? string.Empty;
        var fixApproach = FixApproach.Get(context) ?? string.Empty;
        var filesJson = FilesChangedJson.Get(context) ?? "[]";
        var attempts = Attempts.Get(context);
        var startTimeStr = StartTime.Get(context) ?? DateTime.UtcNow.ToString("o");

        _logger?.LogInformation(
            "Recording debug resolution for session {SessionId}, story {StoryId}: " +
            "rootCause={RootCause}, attempts={Attempts}",
            sessionId, storyId, rootCause, attempts);

        try
        {
            List<string> filesChanged;
            try
            {
                filesChanged = JsonSerializer.Deserialize<List<string>>(filesJson) ?? new();
            }
            catch
            {
                filesChanged = new List<string>();
            }

            var startTime = DateTime.TryParse(startTimeStr, out var parsed)
                ? parsed : DateTime.UtcNow;
            var debuggingTime = DateTime.UtcNow - startTime;

            // Categorize the root cause
            var rootCauseCategory = CategorizeRootCause(rootCause);

            var resolution = new ResolutionData
            {
                RootCauseCategory = rootCauseCategory,
                RootCause = rootCause,
                FixApproach = fixApproach,
                FilesInvolved = filesChanged,
                DebuggingTime = debuggingTime,
                AttemptsToResolution = attempts,
                Mode = Enum.TryParse<DebugContext>(mode, out var ctx) ? ctx : DebugContext.RuntimeError,
                StoryId = storyId
            };

            // Log the resolution event
            if (_repository != null)
            {
                await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
                {
                    SessionId = sessionId,
                    EventType = "debug_resolved",
                    EventData = JsonDocument.Parse(JsonSerializer.Serialize(resolution))
                });
            }

            _logger?.LogInformation(
                "Debug resolution recorded: category={Category}, time={Time:F1}min, " +
                "attempts={Attempts}, files={FileCount}",
                rootCauseCategory, debuggingTime.TotalMinutes, attempts, filesChanged.Count);

            // Log metrics
            _logger?.LogInformation(
                "debug.resolved mode={Mode} story={StoryId} iterations={Iterations} " +
                "duration_seconds={Duration:F0} category={Category}",
                mode, storyId, attempts, debuggingTime.TotalSeconds, rootCauseCategory);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to record debug resolution for session {SessionId}", sessionId);
            // Non-fatal — don't fail the workflow just because recording failed
        }
    }

    private static string CategorizeRootCause(string rootCause)
    {
        var lower = rootCause.ToLower();

        if (lower.Contains("null") || lower.Contains("undefined") || lower.Contains("nil"))
            return "null_reference";
        if (lower.Contains("type") || lower.Contains("cast") || lower.Contains("conversion"))
            return "type_error";
        if (lower.Contains("logic") || lower.Contains("condition") || lower.Contains("branch"))
            return "logic_error";
        if (lower.Contains("async") || lower.Contains("race") || lower.Contains("concurrency"))
            return "concurrency";
        if (lower.Contains("config") || lower.Contains("environment") || lower.Contains("setting"))
            return "configuration";
        if (lower.Contains("dependency") || lower.Contains("import") || lower.Contains("module"))
            return "dependency";
        if (lower.Contains("syntax") || lower.Contains("typo") || lower.Contains("spelling"))
            return "syntax";
        if (lower.Contains("boundary") || lower.Contains("off-by-one") || lower.Contains("overflow"))
            return "boundary_error";
        if (lower.Contains("permission") || lower.Contains("auth") || lower.Contains("access"))
            return "permission";

        return "other";
    }
}
