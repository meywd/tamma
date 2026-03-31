using System.Text.Json;
using System.Text.RegularExpressions;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services;

/// <summary>
/// Implementation of mentorship service
/// </summary>
public partial class MentorshipService : IMentorshipService
{
    private readonly IMentorshipSessionRepository _repository;
    private readonly ILogger<MentorshipService> _logger;

    /// <summary>
    /// Matches any control character (CR, LF, TAB, and all C0/C1 control chars).
    /// Used to prevent log forging by stripping characters that could inject
    /// fake log entries.
    /// </summary>
    [GeneratedRegex(@"[\x00-\x1F\x7F-\x9F]", RegexOptions.Compiled)]
    private static partial Regex ControlCharsRegex();

    public MentorshipService(
        IMentorshipSessionRepository repository,
        ILogger<MentorshipService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Sanitize a string for safe logging by removing all control characters
    /// (newlines, tabs, carriage returns, and other C0/C1 control characters)
    /// in a single atomic pass to prevent log forging.
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        return ControlCharsRegex().Replace(input, "");
    }

    public async Task<MentorshipSession> CreateSessionAsync(string storyId, string juniorId)
    {
        _logger.LogInformation("Creating session for story {StoryId} and junior {JuniorId}", SanitizeForLog(storyId), SanitizeForLog(juniorId));

        var session = new MentorshipSession
        {
            StoryId = storyId,
            JuniorId = juniorId,
            CurrentState = MentorshipState.INIT_STORY_PROCESSING,
            Status = SessionStatus.Active
        };

        return await _repository.CreateAsync(session);
    }

    public async Task<MentorshipSession?> GetSessionAsync(Guid sessionId)
    {
        return await _repository.GetByIdAsync(sessionId);
    }

    public async Task<MentorshipSessionDetails?> GetSessionWithDetailsAsync(Guid sessionId)
    {
        var session = await _repository.GetByIdAsync(sessionId);
        if (session == null) return null;

        var recentEvents = await _repository.GetRecentEventsAsync(sessionId, 20);

        return new MentorshipSessionDetails
        {
            Id = session.Id,
            StoryId = session.StoryId,
            JuniorId = session.JuniorId,
            JuniorName = session.Junior?.Name,
            StoryTitle = session.Story?.Title,
            CurrentState = session.CurrentState,
            PreviousState = session.PreviousState,
            Status = session.Status,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            CompletedAt = session.CompletedAt,
            WorkflowInstanceId = session.WorkflowInstanceId,
            RecentEvents = recentEvents
        };
    }

    public async Task<PagedResult<MentorshipSessionSummary>> GetSessionsAsync(
        int page, int pageSize, string? juniorId = null, string? status = null)
    {
        var (items, totalCount) = await _repository.GetPagedAsync(page, pageSize, juniorId, status);

        var summaries = items.Select(s => new MentorshipSessionSummary
        {
            Id = s.Id,
            StoryId = s.StoryId,
            JuniorId = s.JuniorId,
            JuniorName = s.Junior?.Name,
            StoryTitle = s.Story?.Title,
            CurrentState = s.CurrentState,
            Status = s.Status,
            CreatedAt = s.CreatedAt,
            HoursElapsed = (DateTime.UtcNow - s.CreatedAt).TotalHours,
            EventCount = s.Events?.Count ?? 0
        }).ToList();

        return new PagedResult<MentorshipSessionSummary>
        {
            Items = summaries,
            TotalItems = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task UpdateSessionStateAsync(Guid sessionId, MentorshipState newState, string? reason = null)
    {
        var session = await _repository.GetByIdAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");

        var previousState = session.CurrentState;
        await _repository.UpdateStateAsync(sessionId, newState, previousState);

        // Log the state transition event
        await LogEventAsync(sessionId, EventTypes.StateTransition, new
        {
            previousState = previousState.ToString(),
            newState = newState.ToString(),
            reason
        });

        _logger.LogInformation(
            "Session {SessionId} state changed from {PreviousState} to {NewState}",
            sessionId, previousState, newState);
    }

    public async Task UpdateSessionWorkflowAsync(Guid sessionId, string workflowInstanceId)
    {
        await _repository.UpdateWorkflowInstanceIdAsync(sessionId, workflowInstanceId);
    }

    public async Task UpdateSessionContextAsync(Guid sessionId, object contextUpdate)
    {
        var session = await _repository.GetByIdAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");

        // Merge context update with existing context
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var contextJson = JsonSerializer.Serialize(contextUpdate, jsonOptions);
        session.Context = JsonDocument.Parse(contextJson);

        await _repository.UpdateAsync(session);
    }

    public async Task PauseSessionAsync(Guid sessionId)
    {
        await _repository.UpdateStatusAsync(sessionId, SessionStatus.Paused);
        await LogEventAsync(sessionId, EventTypes.SessionPaused);

        _logger.LogInformation("Session {SessionId} paused", sessionId);
    }

    public async Task ResumeSessionAsync(Guid sessionId)
    {
        await _repository.UpdateStatusAsync(sessionId, SessionStatus.Active);
        await LogEventAsync(sessionId, EventTypes.SessionResumed);

        _logger.LogInformation("Session {SessionId} resumed", sessionId);
    }

    public async Task CancelSessionAsync(Guid sessionId)
    {
        var session = await _repository.GetByIdAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");

        await _repository.UpdateStatusAsync(sessionId, SessionStatus.Cancelled);
        await UpdateSessionStateAsync(sessionId, MentorshipState.CANCELLED, "Session cancelled by user");

        _logger.LogInformation("Session {SessionId} cancelled", sessionId);
    }

    public async Task CompleteSessionAsync(Guid sessionId)
    {
        await _repository.UpdateStatusAsync(sessionId, SessionStatus.Completed);
        await UpdateSessionStateAsync(sessionId, MentorshipState.COMPLETED, "Session completed successfully");
        await LogEventAsync(sessionId, EventTypes.SessionCompleted);

        _logger.LogInformation("Session {SessionId} completed", sessionId);
    }

    public async Task FailSessionAsync(Guid sessionId, string reason)
    {
        await _repository.UpdateStatusAsync(sessionId, SessionStatus.Failed);
        await UpdateSessionStateAsync(sessionId, MentorshipState.FAILED, reason);
        await LogEventAsync(sessionId, EventTypes.SessionFailed, new { reason });

        _logger.LogWarning("Session {SessionId} failed: {Reason}", sessionId, reason);
    }

    public async Task<List<MentorshipEvent>> GetSessionEventsAsync(Guid sessionId)
    {
        return await _repository.GetEventsBySessionIdAsync(sessionId);
    }

    public async Task LogEventAsync(Guid sessionId, string eventType, object? eventData = null)
    {
        var eventRecord = new MentorshipEvent
        {
            SessionId = sessionId,
            EventType = eventType,
            EventData = eventData != null
                ? JsonDocument.Parse(JsonSerializer.Serialize(eventData))
                : null
        };

        await _repository.LogEventAsync(eventRecord);
    }

    public async Task<DashboardAnalytics> GetDashboardAnalyticsAsync()
    {
        var today = DateTime.UtcNow.Date;
        var weekAgo = today.AddDays(-7);

        var activeSessions = await _repository.GetActiveSessionCountAsync();
        var completedToday = await _repository.GetCompletedSessionCountAsync(today);
        var completedThisWeek = await _repository.GetCompletedSessionCountAsync(weekAgo);
        var avgCompletionTime = await _repository.GetAverageCompletionTimeAsync(weekAgo);
        var sessionsByState = await _repository.GetSessionCountByStateAsync();

        // Calculate success rate
        var totalCompleted = completedThisWeek;
        var activePlusFailed = activeSessions; // Simplified
        var successRate = totalCompleted > 0 ? (double)totalCompleted / (totalCompleted + activePlusFailed) * 100 : 0;

        return new DashboardAnalytics
        {
            ActiveSessions = activeSessions,
            CompletedToday = completedToday,
            CompletedThisWeek = completedThisWeek,
            AverageCompletionHours = avgCompletionTime,
            SuccessRate = successRate,
            SessionsByState = sessionsByState
        };
    }
}
