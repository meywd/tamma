using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Core;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 32-1 — CP-resident agent repository. Owns the agent identity +
/// versioning lifecycle (<see cref="Agent"/> / <see cref="AgentVersion"/>) and
/// emits the Epic 32 DCB audit events. Resolves against
/// <see cref="ControlPlaneDbContext"/>; DCB events are appended via
/// <see cref="IEventRepository"/> only after a real state transition.
///
/// <para>Event routing follows the design: <c>DomainEvent.TenantId =
/// OwnerTenantId</c> for private/SaaS agents (lands in the tenant store) and
/// NULL for public/system agents (lands in <c>platform_events</c>) — the
/// <see cref="IEventRepository"/> handles the split.</para>
///
/// <para>Never logs raw <see cref="AgentVersion.ConfigJson"/> — configs are
/// credential-agnostic by design but a <c>systemPromptRef</c> could resolve to
/// sensitive content, so only field-level diagnostics are logged.</para>
/// </summary>
public sealed class AgentRepository : IAgentRepository
{
    private const string WorkflowVersion = "1.0.0";

    /// <summary>Bounded retry budget for the concurrent-publish race.</summary>
    private const int MaxPublishRetries = 5;

    private readonly ControlPlaneDbContext _db;
    private readonly IEventRepository _events;
    private readonly ILogger<AgentRepository>? _logger;

    public AgentRepository(
        ControlPlaneDbContext db,
        IEventRepository events,
        ILogger<AgentRepository>? logger = null)
    {
        _db = db;
        _events = events;
        _logger = logger;
    }

    public async Task<Agent> CreateAsync(
        Agent agent,
        string firstVersionConfigJson,
        string? notes,
        Guid? createdBy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Entity-level ownership guard (fail-fast before the DB CHECK). Mirrors
        // CLAUDE.md "Universal rule for any tenant-aware feature": public ⇒ no
        // owner; private ⇒ exactly one owner column. The endpoint layer derives
        // WHICH owner column from the process mode; here we enforce the XOR.
        AssertOwnershipInvariant(agent);

        if (string.IsNullOrWhiteSpace(firstVersionConfigJson))
        {
            throw new TammaError(
                "AGENT.CREATE.EMPTY_CONFIG",
                "First version config must not be empty.",
                severity: TammaErrorSeverity.Medium);
        }

        var now = DateTime.UtcNow;
        agent.Id = agent.Id == Guid.Empty ? Guid.NewGuid() : agent.Id;
        agent.Status = AgentStatus.Active;
        agent.CreatedAt = now;
        agent.CreatedBy = createdBy;
        agent.UpdatedAt = now;
        agent.UpdatedBy = createdBy;

        var version = new AgentVersion
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            Version = 1,
            ConfigJson = firstVersionConfigJson,
            Notes = notes,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
        agent.CurrentVersionId = version.Id;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.Agents.Add(agent);
        _db.AgentVersions.Add(version);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger?.LogInformation(
            "agent.created agentId={AgentId} name={Name} role={Role} visibility={Visibility} version=1",
            agent.Id, agent.Name, agent.Role, agent.Visibility);

        await AppendAgentEventAsync(
            "AGENT.CREATED.SUCCESS", agent, version.Version, ct);

        return agent;
    }

    public async Task<AgentVersion?> PublishVersionAsync(
        Guid agentId,
        string configJson,
        string? notes,
        Guid? updatedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            throw new TammaError(
                "AGENT.PUBLISH.EMPTY_CONFIG",
                "Published config must not be empty.",
                severity: TammaErrorSeverity.Medium);
        }

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null)
        {
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            var nextVersion = await NextVersionAsync(agentId, ct);
            var version = new AgentVersion
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Version = nextVersion,
                ConfigJson = configJson,
                Notes = notes,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = updatedBy,
            };

            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                _db.AgentVersions.Add(version);
                agent.CurrentVersionId = version.Id;
                agent.UpdatedAt = DateTime.UtcNow;
                agent.UpdatedBy = updatedBy;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger?.LogInformation(
                    "agent.version_published agentId={AgentId} version={Version}",
                    agentId, version.Version);

                await AppendAgentEventAsync(
                    "AGENT.VERSION_PUBLISHED.SUCCESS", agent, version.Version, ct);

                return version;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < MaxPublishRetries)
            {
                // Concurrent double-publish: another writer took (AgentId,
                // nextVersion). Detach the failed insert, recompute max+1, retry.
                _db.Entry(version).State = EntityState.Detached;
                _db.Entry(agent).State = EntityState.Unchanged;
                _logger?.LogWarning(
                    "agent.version_published.retry agentId={AgentId} attempt={Attempt} contendedVersion={Version}",
                    agentId, attempt, nextVersion);
            }
        }
    }

    public async Task<Agent?> ArchiveAsync(
        Guid agentId, Guid? updatedBy, CancellationToken ct = default)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null)
        {
            return null;
        }

        // Idempotent — archiving an already-archived agent is a no-op with no
        // second AGENT.ARCHIVED.SUCCESS (events only on real transitions).
        if (agent.Status == AgentStatus.Archived)
        {
            return agent;
        }

        agent.Status = AgentStatus.Archived;
        agent.UpdatedAt = DateTime.UtcNow;
        agent.UpdatedBy = updatedBy;
        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "agent.archived agentId={AgentId} visibility={Visibility} role={Role}",
            agent.Id, agent.Visibility, agent.Role);

        await AppendAgentEventAsync("AGENT.ARCHIVED.SUCCESS", agent, version: null, ct);
        return agent;
    }

    public Task<Agent?> GetByIdAsync(Guid agentId, CancellationToken ct = default)
        => _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);

    public Task<AgentVersion?> GetVersionAsync(
        Guid agentId, int version, CancellationToken ct = default)
        => _db.AgentVersions
            .FirstOrDefaultAsync(v => v.AgentId == agentId && v.Version == version, ct);

    public async Task<IReadOnlyList<AgentVersion>> ListVersionsAsync(
        Guid agentId, CancellationToken ct = default)
        => await _db.AgentVersions
            .Where(v => v.AgentId == agentId)
            .OrderBy(v => v.Version)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Agent>> ListVisibleAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default)
    {
        var query = _db.Agents.Where(a =>
            a.Visibility == AgentVisibility.Public ||
            (a.Visibility == AgentVisibility.Private &&
                ((tenantId != null && a.OwnerTenantId == tenantId) ||
                 (userId != null && a.OwnerUserId == userId))));

        var result = await query.OrderBy(a => a.Name).ToListAsync(ct);

        _logger?.LogDebug(
            "agent.list_visible count={Count} tenantId={TenantId} userId={UserId}",
            result.Count, tenantId, userId);

        return result;
    }

    // ── helpers ──

    private async Task<int> NextVersionAsync(Guid agentId, CancellationToken ct)
    {
        var max = await _db.AgentVersions
            .Where(v => v.AgentId == agentId)
            .Select(v => (int?)v.Version)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private static void AssertOwnershipInvariant(Agent agent)
    {
        var hasTenant = agent.OwnerTenantId is not null;
        var hasUser = agent.OwnerUserId is not null;

        switch (agent.Visibility)
        {
            case AgentVisibility.Public when hasTenant || hasUser:
                throw new TammaError(
                    "AGENT.OWNERSHIP.PUBLIC_WITH_OWNER",
                    "A public agent must not have an owner (tenant or user).",
                    new Dictionary<string, object?> { ["agentName"] = agent.Name },
                    severity: TammaErrorSeverity.High);
            case AgentVisibility.Private when hasTenant == hasUser:
                // both set OR both null
                throw new TammaError(
                    "AGENT.OWNERSHIP.PRIVATE_PRINCIPAL",
                    "A private agent must have exactly one owner — OwnerTenantId "
                    + "(SaaS) XOR OwnerUserId (single-user).",
                    new Dictionary<string, object?> { ["agentName"] = agent.Name },
                    severity: TammaErrorSeverity.High);
        }
    }

    /// <summary>
    /// Append an Epic 32 agent DCB event. <c>TenantId = OwnerTenantId</c> for
    /// private/SaaS agents (tenant store), NULL for public agents
    /// (<c>platform_events</c>). Tags carry the agent shape per the design.
    /// </summary>
    private async Task AppendAgentEventAsync(
        string type, Agent agent, int? version, CancellationToken ct)
    {
        _ = ct;
        var mode = agent.OwnerUserId is not null ? "single-user" : "saas";

        var tags = new Dictionary<string, object?>
        {
            ["agentId"] = agent.Id.ToString(),
            ["visibility"] = agent.Visibility == AgentVisibility.Public ? "public" : "private",
            ["role"] = agent.Role,
            ["mode"] = mode,
        };
        if (version is int v) tags["version"] = v;
        if (agent.OwnerTenantId is Guid otid) tags["ownerTenantId"] = otid.ToString();
        if (agent.OwnerUserId is Guid ouid) tags["ownerUserId"] = ouid.ToString();

        await _events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            // Public agents → null tenant (platform feed); private → owner tenant.
            TenantId = agent.OwnerTenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = WorkflowVersion,
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(version is int dv
                ? new Dictionary<string, object?> { ["version"] = dv }
                : new Dictionary<string, object?>()),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg &&
           pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
}
