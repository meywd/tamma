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
/// Fetches the history of events for the current mentorship session.
/// Returns state transitions and recent events for context about the session's progress.
/// </summary>
[Activity(
    "Tamma.Context",
    "Fetch Session History",
    "Retrieve mentorship session event history and state transitions",
    Kind = ActivityKind.Task
)]
public class FetchSessionHistoryActivity : CodeActivity<SessionHistoryResult>
{
    private readonly ILogger<FetchSessionHistoryActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Maximum number of events to return</summary>
    [Input(Description = "Maximum events to return", DefaultValue = 20)]
    public Input<int> MaxEvents { get; set; } = new(20);

    [JsonConstructor]
    public FetchSessionHistoryActivity()
    {
    }

    public FetchSessionHistoryActivity(
        ILogger<FetchSessionHistoryActivity> logger,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var maxEvents = MaxEvents.Get(context);

        _logger?.LogInformation(
            "Fetching session history for session {SessionId}", sessionId);

        try
        {
            var events = await _repository!.GetEventsBySessionIdAsync(sessionId);

            var sessionEvents = events
                .OrderByDescending(e => e.CreatedAt)
                .Take(maxEvents)
                .Select(e => new SessionEvent
                {
                    EventType = e.EventType,
                    Timestamp = e.CreatedAt,
                    StateFrom = e.StateFrom?.ToString(),
                    StateTo = e.StateTo?.ToString()
                })
                .ToList();

            context.SetResult(new SessionHistoryResult
            {
                TotalEvents = events.Count,
                Events = sessionEvents,
                Success = true
            });

            _logger?.LogInformation(
                "Fetched {Count} events (of {Total}) for session {SessionId}",
                sessionEvents.Count, events.Count, sessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to fetch session history for {SessionId}", sessionId);
            context.SetResult(new SessionHistoryResult
            {
                Success = false,
                ErrorMessage = $"Failed to fetch session history: {ex.Message}"
            });
        }
    }
}
