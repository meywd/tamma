using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Core.Enums;

namespace Tamma.Data.Repositories;

/// <summary>
/// Repository for mentorship session data access.
///
/// <para>Story 28-1 PR D: mentorship_sessions, mentorship_events,
/// junior_developers, stories all moved off
/// <see cref="ControlPlaneDbContext"/>. Every operation now requires an
/// ambient tenant id; system-scope / admin paths must bind a tenant
/// before invoking the repository.</para>
/// </summary>
public class MentorshipSessionRepository : IMentorshipSessionRepository, IAsyncDisposable
{
    private readonly ITenantDbContextFactory _factory;
    private readonly ITenantContext _tenantContext;
    private TenantDbContext? _cachedTenantCtx;

    public MentorshipSessionRepository(
        ITenantDbContextFactory factory,
        ITenantContext tenantContext)
    {
        _factory = factory;
        _tenantContext = tenantContext;
    }

    private Guid RequireTenantId() => _tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "MentorshipSessionRepository requires an ambient tenant id. " +
            "Story 28-1 PR D moved mentorship_* and stories / junior_developers " +
            "off the control plane; admin / system paths must bind a tenant " +
            "before calling.");

    private async Task<TenantDbContext> GetCtxAsync()
    {
        var tid = RequireTenantId();
        _cachedTenantCtx ??= await _factory.CreateAsync(tid);
        return _cachedTenantCtx;
    }

    private async Task<DbSet<MentorshipSession>> Sessions()
        => (await GetCtxAsync()).MentorshipSessions;

    private async Task<DbSet<MentorshipEvent>> Events()
        => (await GetCtxAsync()).MentorshipEvents;

    private async Task SaveAsync()
    {
        var ctx = await GetCtxAsync();
        await ctx.SaveChangesAsync();
    }

    public async Task<MentorshipSession> CreateAsync(MentorshipSession session)
    {
        (await Sessions()).Add(session);
        await SaveAsync();
        return session;
    }

    public async Task<MentorshipSession?> GetByIdAsync(Guid id)
    {
        return await (await Sessions())
            .Include(s => s.Junior)
            .Include(s => s.Story)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<MentorshipSession?> GetByWorkflowInstanceIdAsync(string workflowInstanceId)
    {
        return await (await Sessions())
            .Include(s => s.Junior)
            .Include(s => s.Story)
            .FirstOrDefaultAsync(s => s.WorkflowInstanceId == workflowInstanceId);
    }

    public async Task<List<MentorshipSession>> GetByJuniorIdAsync(string juniorId)
    {
        return await (await Sessions())
            .Include(s => s.Story)
            .Where(s => s.JuniorId == juniorId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<MentorshipSession>> GetByStoryIdAsync(string storyId)
    {
        return await (await Sessions())
            .Include(s => s.Junior)
            .Where(s => s.StoryId == storyId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<MentorshipSession>> GetActiveSessionsAsync()
    {
        return await (await Sessions())
            .Include(s => s.Junior)
            .Include(s => s.Story)
            .Where(s => s.Status == SessionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<MentorshipSession>> GetSessionsByStatusAsync(SessionStatus status)
    {
        return await (await Sessions())
            .Include(s => s.Junior)
            .Include(s => s.Story)
            .Where(s => s.Status == status)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<(List<MentorshipSession> Items, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        string? juniorId = null,
        string? status = null)
    {
        var query = (await Sessions())
            .Include(s => s.Junior)
            .Include(s => s.Story)
            .AsQueryable();

        if (!string.IsNullOrEmpty(juniorId))
        {
            query = query.Where(s => s.JuniorId == juniorId);
        }

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SessionStatus>(status, true, out var sessionStatus))
        {
            query = query.Where(s => s.Status == sessionStatus);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task UpdateAsync(MentorshipSession session)
    {
        session.UpdatedAt = DateTime.UtcNow;
        (await Sessions()).Update(session);
        await SaveAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var sessions = await Sessions();
        var session = await sessions.FindAsync(id)
            ?? throw new KeyNotFoundException($"Session {id} not found");
        sessions.Remove(session);
        await SaveAsync();
    }

    public async Task UpdateStateAsync(Guid sessionId, MentorshipState newState, MentorshipState? previousState = null)
    {
        var sessions = await Sessions();
        var session = await sessions.FindAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        session.PreviousState = previousState ?? session.CurrentState;
        session.CurrentState = newState;
        session.UpdatedAt = DateTime.UtcNow;
        await SaveAsync();
    }

    public async Task UpdateStatusAsync(Guid sessionId, SessionStatus status)
    {
        var sessions = await Sessions();
        var session = await sessions.FindAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        session.Status = status;
        session.UpdatedAt = DateTime.UtcNow;

        if (status == SessionStatus.Completed)
        {
            session.CompletedAt = DateTime.UtcNow;
        }

        await SaveAsync();
    }

    public async Task UpdateWorkflowInstanceIdAsync(Guid sessionId, string workflowInstanceId)
    {
        var sessions = await Sessions();
        var session = await sessions.FindAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        session.WorkflowInstanceId = workflowInstanceId;
        session.UpdatedAt = DateTime.UtcNow;
        await SaveAsync();
    }

    public async Task<MentorshipEvent> LogEventAsync(MentorshipEvent eventRecord)
    {
        (await Events()).Add(eventRecord);
        await SaveAsync();
        return eventRecord;
    }

    public async Task<List<MentorshipEvent>> GetEventsBySessionIdAsync(Guid sessionId)
    {
        return await (await Events())
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<MentorshipEvent>> GetRecentEventsAsync(Guid sessionId, int count = 10)
    {
        return await (await Events())
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    // Story 28-1 PR D: juniors and stories moved to the per-tenant DB.
    public async Task<JuniorDeveloper?> GetJuniorByIdAsync(string id)
    {
        var ctx = await GetCtxAsync();
        return await ctx.JuniorDevelopers.FindAsync(id);
    }

    public async Task<JuniorDeveloper> CreateJuniorAsync(JuniorDeveloper junior)
    {
        var ctx = await GetCtxAsync();
        ctx.JuniorDevelopers.Add(junior);
        await ctx.SaveChangesAsync();
        return junior;
    }

    public async Task UpdateJuniorAsync(JuniorDeveloper junior)
    {
        var ctx = await GetCtxAsync();
        junior.UpdatedAt = DateTime.UtcNow;
        ctx.JuniorDevelopers.Update(junior);
        await ctx.SaveChangesAsync();
    }

    public async Task<List<JuniorDeveloper>> GetAllJuniorsAsync()
    {
        var ctx = await GetCtxAsync();
        return await ctx.JuniorDevelopers.OrderBy(j => j.Name).ToListAsync();
    }

    public async Task<Story?> GetStoryByIdAsync(string id)
    {
        var ctx = await GetCtxAsync();
        return await ctx.Stories.FindAsync(id);
    }

    public async Task<Story> CreateStoryAsync(Story story)
    {
        var ctx = await GetCtxAsync();
        ctx.Stories.Add(story);
        await ctx.SaveChangesAsync();
        return story;
    }

    public async Task UpdateStoryAsync(Story story)
    {
        var ctx = await GetCtxAsync();
        story.UpdatedAt = DateTime.UtcNow;
        ctx.Stories.Update(story);
        await ctx.SaveChangesAsync();
    }

    public async Task<List<Story>> GetAllStoriesAsync()
    {
        var ctx = await GetCtxAsync();
        return await ctx.Stories.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    public async Task<int> GetActiveSessionCountAsync()
        => await (await Sessions()).CountAsync(s => s.Status == SessionStatus.Active);

    public async Task<int> GetCompletedSessionCountAsync(DateTime since)
        => await (await Sessions()).CountAsync(
            s => s.Status == SessionStatus.Completed && s.CompletedAt >= since);

    public async Task<double> GetAverageCompletionTimeAsync(DateTime since)
    {
        var completedSessions = await (await Sessions())
            .Where(s => s.Status == SessionStatus.Completed &&
                        s.CompletedAt >= since &&
                        s.CompletedAt != null)
            .Select(s => new { s.CreatedAt, s.CompletedAt })
            .ToListAsync();

        if (!completedSessions.Any())
            return 0;

        return completedSessions
            .Average(s => (s.CompletedAt!.Value - s.CreatedAt).TotalHours);
    }

    public async Task<Dictionary<MentorshipState, int>> GetSessionCountByStateAsync()
    {
        return await (await Sessions())
            .Where(s => s.Status == SessionStatus.Active)
            .GroupBy(s => s.CurrentState)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    public async ValueTask DisposeAsync()
    {
        if (_cachedTenantCtx is not null)
        {
            await _cachedTenantCtx.DisposeAsync();
            _cachedTenantCtx = null;
        }
    }
}
