using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-6 (T3) — <see cref="AgentTrailEndpoints"/> projection + paging wire
/// shape. The repository is a capturing double (no DB) so we assert the endpoint
/// passes the right filters (typePrefix, agentId, tenantId) and projects the
/// event rows into the run/trail DTOs with correct <c>nextCursor</c>/<c>hasMore</c>.
/// The tenant-isolation guarantee itself is proven at the repository level
/// (<see cref="AgentTrailRepositoryTests"/>) and by the route's
/// <c>RequireTenantMembershipFilter</c> (reused, mapped in Program.cs).
/// </summary>
[TestFixture]
public class AgentTrailEndpointsTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa");
    private static readonly Guid Agent = Guid.Parse("bbbbbbbb-0000-0000-0000-bbbbbbbbbbbb");

    private static HttpContext Http() => new DefaultHttpContext();

    private static DomainEvent TaskEvent(long seq, string type, Guid agentId, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        TenantId = Tenant,
        SequenceNumber = seq,
        CreatedAt = createdAt,
        Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["agentId"] = agentId.ToString(),
            ["agentVersion"] = "7",
            ["role"] = "developer",
            ["provider"] = "anthropic",
            ["model"] = "claude-sonnet-4",
            ["correlationId"] = "corr-1",
            ["issueId"] = "ISSUE-9",
            ["credentialSource"] = "platform",
        }),
        Data = JsonSerializer.Serialize(new
        {
            durationMs = 999L,
            iterations = 2,
            inputTokens = 10,
            outputTokens = 5,
            costUsd = 0.0012m,
        }),
    };

    // ── /runs projects AGENT.TASK.* → AgentRunDto ───────────────────────

    [Test]
    public async Task ListRuns_ProjectsRunDtos_AndPassesTaskPrefix()
    {
        var repo = new CapturingRepo(new[]
        {
            TaskEvent(30, "AGENT.TASK.SUCCESS", Agent, DateTime.UtcNow),
            TaskEvent(29, "AGENT.TASK.FAILED", Agent, DateTime.UtcNow),
        }, total: 2);

        var result = await AgentTrailEndpoints.ListRuns(Http(), repo, Tenant, Agent, limit: 50);

        repo.LastTenantId.Should().Be(Tenant);
        repo.LastAgentId.Should().Be(Agent);
        repo.LastTypePrefix.Should().Be("AGENT.TASK");

        var page = ((Ok<AgentTrailPage<AgentRunDto>>)result).Value!;
        page.Total.Should().Be(2);
        page.Items.Should().HaveCount(2);
        page.Items[0].Outcome.Should().Be("success");
        page.Items[0].Provider.Should().Be("anthropic");
        page.Items[0].InputTokens.Should().Be(10);
        page.Items[0].DurationMs.Should().Be(999);
        page.Items[1].Outcome.Should().Be("failed");
        // 2 rows < take(50) ⇒ no next page.
        page.HasMore.Should().BeFalse();
        page.NextCursor.Should().BeNull();
    }

    [Test]
    public async Task ListRuns_FullPage_SetsNextCursorToLastSequenceNumber()
    {
        var rows = Enumerable.Range(0, 2)
            .Select(i => TaskEvent(20 - i, "AGENT.TASK.SUCCESS", Agent, DateTime.UtcNow))
            .ToArray();
        var repo = new CapturingRepo(rows, total: 10);

        var result = await AgentTrailEndpoints.ListRuns(Http(), repo, Tenant, Agent, limit: 2);

        var page = ((Ok<AgentTrailPage<AgentRunDto>>)result).Value!;
        // rows.Count == take(2) ⇒ more pages; cursor is the last (smallest) seq.
        page.HasMore.Should().BeTrue();
        page.NextCursor.Should().Be(19);
        repo.LastCursor.Should().BeNull("first page has no cursor");
        repo.LastLimit.Should().Be(2);
    }

    [Test]
    public async Task ListRuns_PassesFiltersThrough()
    {
        var repo = new CapturingRepo(Array.Empty<DomainEvent>(), total: 0);
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow;

        await AgentTrailEndpoints.ListRuns(Http(), repo, Tenant, Agent,
            from: from, to: to, role: "developer", provider: "openai", outcome: "failed",
            cursor: 500, limit: 25);

        repo.LastRole.Should().Be("developer");
        repo.LastProvider.Should().Be("openai");
        repo.LastOutcome.Should().Be("failed");
        repo.LastFrom.Should().Be(from);
        repo.LastTo.Should().Be(to);
        repo.LastCursor.Should().Be(500);
        repo.LastLimit.Should().Be(25);
    }

    [Test]
    public async Task ListRuns_ClampsLimitToMax500()
    {
        var repo = new CapturingRepo(Array.Empty<DomainEvent>(), total: 0);
        await AgentTrailEndpoints.ListRuns(Http(), repo, Tenant, Agent, limit: 100000);
        repo.LastLimit.Should().Be(500);
    }

    // ── /trail projects the full family ─────────────────────────────────

    [Test]
    public async Task ListTrail_ProjectsTrailDtos_AndDefaultsToNoTypeFilter()
    {
        var repo = new CapturingRepo(new[]
        {
            TaskEvent(40, "AGENT.TOOL_CALL.SUCCESS", Agent, DateTime.UtcNow),
        }, total: 1);

        var result = await AgentTrailEndpoints.ListTrail(Http(), repo, Tenant, Agent, limit: 50);

        repo.LastTypePrefix.Should().BeNull("trail defaults to all agentId-tagged events");
        var page = ((Ok<AgentTrailPage<AgentTrailEventDto>>)result).Value!;
        page.Items.Should().ContainSingle();
        page.Items[0].Type.Should().Be("AGENT.TOOL_CALL.SUCCESS");
        page.Items[0].AgentId.Should().Be(Agent.ToString());
    }

    [Test]
    public async Task ListTrail_ForwardsExplicitTypePrefix()
    {
        var repo = new CapturingRepo(Array.Empty<DomainEvent>(), total: 0);
        await AgentTrailEndpoints.ListTrail(Http(), repo, Tenant, Agent, type: "AGENT.TOOL_CALL");
        repo.LastTypePrefix.Should().Be("AGENT.TOOL_CALL");
    }

    // ── Validation ──────────────────────────────────────────────────────

    [Test]
    public async Task ListRuns_EmptyTenant_BadRequest()
    {
        var repo = new CapturingRepo(Array.Empty<DomainEvent>(), total: 0);
        var result = await AgentTrailEndpoints.ListRuns(Http(), repo, Guid.Empty, Agent);
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task ListRuns_EmptyAgent_BadRequest()
    {
        var repo = new CapturingRepo(Array.Empty<DomainEvent>(), total: 0);
        var result = await AgentTrailEndpoints.ListRuns(Http(), repo, Tenant, Guid.Empty);
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
    }

    // ── Capturing repository double ─────────────────────────────────────

    private sealed class CapturingRepo : IEventRepository
    {
        private readonly IReadOnlyList<DomainEvent> _rows;
        private readonly int _total;

        public CapturingRepo(IReadOnlyList<DomainEvent> rows, int total)
        {
            _rows = rows;
            _total = total;
        }

        public Guid LastTenantId { get; private set; }
        public Guid LastAgentId { get; private set; }
        public string? LastTypePrefix { get; private set; }
        public string? LastRole { get; private set; }
        public string? LastProvider { get; private set; }
        public string? LastOutcome { get; private set; }
        public DateTimeOffset? LastFrom { get; private set; }
        public DateTimeOffset? LastTo { get; private set; }
        public long? LastCursor { get; private set; }
        public int LastLimit { get; private set; }

        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryAgentTrailAsync(
            Guid tenantId, Guid agentId, string? typePrefix,
            DateTimeOffset? from, DateTimeOffset? to,
            string? role, string? provider, string? outcome,
            long? cursor, int limit)
        {
            LastTenantId = tenantId;
            LastAgentId = agentId;
            LastTypePrefix = typePrefix;
            LastRole = role;
            LastProvider = provider;
            LastOutcome = outcome;
            LastFrom = from;
            LastTo = to;
            LastCursor = cursor;
            LastLimit = limit;
            return Task.FromResult((_rows, _total));
        }

        public Task<DomainEvent> AppendAsync(DomainEvent evt) => Task.FromResult(evt);
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
