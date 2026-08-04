using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Infrastructure;
using Tamma.Api.Models;
using Tamma.Api.Services;
using Tamma.Core.Actions;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Controllers;

/// <summary>
/// Controller for managing mentorship sessions
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public partial class MentorshipController : ControllerBase
{
    private readonly IMentorshipService _mentorshipService;
    private readonly IElsaWorkflowService _elsaService;
    private readonly ILogger<MentorshipController> _logger;

    /// <summary>
    /// Matches any control character (CR, LF, TAB, and all C0/C1 control chars).
    /// Used to prevent log forging by stripping characters that could inject
    /// fake log entries.
    /// </summary>
    [GeneratedRegex(@"[\x00-\x1F\x7F-\x9F]", RegexOptions.Compiled)]
    private static partial Regex ControlCharsRegex();

    public MentorshipController(
        IMentorshipService mentorshipService,
        IElsaWorkflowService elsaService,
        ILogger<MentorshipController> logger)
    {
        _mentorshipService = mentorshipService;
        _elsaService = elsaService;
        _logger = logger;
    }

    /// <summary>
    /// Sanitize a string for safe logging by removing all control characters
    /// in a single atomic pass to prevent log forging.
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        return ControlCharsRegex().Replace(input, "");
    }

    /// <summary>
    /// Start a new mentorship session
    /// </summary>
    [HttpPost("start")]
    // Story 43-8 AC1 step 2 — the [Governs] authoring shape, on the repo's only
    // attribute-routed controller. The wire string is checked against
    // ActionCatalog.ByKey AND against this route's own pattern (SiteKey ==
    // "{METHOD} {RawText}") by GovernedEndpointBindingSweepTests, so a typo or a
    // binding to somebody else's action fails the build rather than silently
    // governing nothing. Metadata only: Story 43-9 attaches the enforcement filter.
    [Governs(ActionNamespace.Effect, "mentorship.session.start")]
    [ProducesResponseType(typeof(MentorshipStartResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<MentorshipStartResponse>> StartMentorship(
        [FromBody] StartMentorshipRequest request)
    {
        try
        {
            // Validate request
            if (string.IsNullOrEmpty(request.StoryId) || string.IsNullOrEmpty(request.JuniorId))
            {
                return BadRequest("Story ID and Junior ID are required");
            }

            // Create mentorship session
            var session = await _mentorshipService.CreateSessionAsync(request.StoryId, request.JuniorId);

            // Start ELSA workflow
            var workflowInput = new Dictionary<string, object>
            {
                ["storyId"] = request.StoryId,
                ["juniorId"] = request.JuniorId,
                ["sessionId"] = session.Id
            };

            var workflowInstanceId = await _elsaService.StartWorkflowAsync(
                "tamma-autonomous-mentorship",
                workflowInput);

            // Update session with workflow instance ID
            await _mentorshipService.UpdateSessionWorkflowAsync(session.Id, workflowInstanceId);

            _logger.LogInformation(
                "Started mentorship session {SessionId} for story {StoryId} and junior {JuniorId}",
                session.Id, SanitizeForLog(request.StoryId), SanitizeForLog(request.JuniorId));

            return Ok(new MentorshipStartResponse
            {
                SessionId = session.Id,
                WorkflowInstanceId = workflowInstanceId,
                Status = "started",
                CurrentState = "INIT_STORY_PROCESSING"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mentorship session");
            return StatusCode(500, "Failed to start mentorship session");
        }
    }

    /// <summary>
    /// Get a specific session by ID
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}")]
    [ProducesResponseType(typeof(MentorshipSessionDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MentorshipSessionDetails>> GetSession(Guid sessionId)
    {
        var session = await _mentorshipService.GetSessionWithDetailsAsync(sessionId);
        if (session == null)
            return NotFound();

        return Ok(session);
    }

    /// <summary>
    /// Get paginated list of sessions
    /// </summary>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(PagedResult<MentorshipSessionSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MentorshipSessionSummary>>> GetSessions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? juniorId = null,
        [FromQuery] string? status = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _mentorshipService.GetSessionsAsync(page, pageSize, juniorId, status);
        return Ok(result);
    }

    /// <summary>
    /// Pause a session
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/pause")]
    [Governs(ActionNamespace.Effect, "mentorship.session.pause")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PauseSession(Guid sessionId)
    {
        try
        {
            await _mentorshipService.PauseSessionAsync(sessionId);
            await _elsaService.PauseWorkflowAsync(sessionId.ToString());

            _logger.LogInformation("Paused session {SessionId}", sessionId);
            return Ok(new { message = "Session paused successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause session {SessionId}", sessionId);
            return StatusCode(500, "Failed to pause session");
        }
    }

    /// <summary>
    /// Resume a paused session
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/resume")]
    [Governs(ActionNamespace.Effect, "mentorship.session.resume")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ResumeSession(Guid sessionId)
    {
        try
        {
            await _mentorshipService.ResumeSessionAsync(sessionId);
            await _elsaService.ResumeWorkflowAsync(sessionId.ToString());

            _logger.LogInformation("Resumed session {SessionId}", sessionId);
            return Ok(new { message = "Session resumed successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume session {SessionId}", sessionId);
            return StatusCode(500, "Failed to resume session");
        }
    }

    /// <summary>
    /// Cancel a session
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/cancel")]
    [Governs(ActionNamespace.Effect, "mentorship.session.cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> CancelSession(Guid sessionId)
    {
        try
        {
            await _mentorshipService.CancelSessionAsync(sessionId);
            await _elsaService.CancelWorkflowAsync(sessionId.ToString());

            _logger.LogInformation("Cancelled session {SessionId}", sessionId);
            return Ok(new { message = "Session cancelled successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel session {SessionId}", sessionId);
            return StatusCode(500, "Failed to cancel session");
        }
    }

    /// <summary>
    /// Get events for a session
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/events")]
    [ProducesResponseType(typeof(List<Core.Entities.MentorshipEvent>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<Core.Entities.MentorshipEvent>>> GetSessionEvents(Guid sessionId)
    {
        var events = await _mentorshipService.GetSessionEventsAsync(sessionId);
        return Ok(events);
    }

    /// <summary>
    /// Get dashboard analytics
    /// </summary>
    [HttpGet("analytics/dashboard")]
    [ProducesResponseType(typeof(DashboardAnalytics), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardAnalytics>> GetDashboardAnalytics()
    {
        var analytics = await _mentorshipService.GetDashboardAnalyticsAsync();
        return Ok(analytics);
    }
}
