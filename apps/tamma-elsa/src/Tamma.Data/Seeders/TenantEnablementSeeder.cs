using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Story 32-16 (AC10) — seeds a FRESH tenant (SaaS) or user (single-user) with
/// the platform DEFAULT PERSONA enabled, so a brand-new principal has a usable
/// catalog out of the box (enablement is otherwise default-deny for public
/// personas). This is the <i>seeding hook</i>; the resolve-time fail-loud when a
/// principal explicitly disables everything lives in sibling story 32-18.
///
/// <para><b>Insert-missing-only.</b> The seeder writes an enablement row for the
/// default persona ONLY when the principal has NO existing row for it — it NEVER
/// reverts an explicit disable (an <c>Enabled = false</c> row is left untouched).
/// Re-running is a no-op. Mirrors <see cref="AgentEntitySeeder"/> /
/// <see cref="ConventionStoreSeeder"/> insert-missing-only discipline.</para>
///
/// <para><b>CP-resident, both modes.</b> <c>tenant_agent_enablements</c> is
/// control-plane resident in BOTH modes — SaaS rows keyed by <c>TenantId</c>,
/// single-user rows by <c>UserId</c> (principal XOR). The seeder writes against
/// <see cref="ControlPlaneDbContext"/> directly.</para>
///
/// <para>This seeder lives in <c>Tamma.Data</c> and cannot reference the
/// <c>Tamma.Api</c> <c>DefaultPersonaOptions</c>, so the default persona handle
/// is passed in (the caller binds <c>Tamma:Agents:DefaultPersonaName</c>). The
/// default persona must already be seeded (Story 32-15) — this seeder MUST run
/// AFTER <see cref="AgentEntitySeeder"/>; a missing/inactive default persona is
/// WARN-logged and skipped (no half-seeded row).</para>
/// </summary>
public static class TenantEnablementSeeder
{
    /// <summary>
    /// Enable the platform default persona for a fresh principal, insert-missing-only.
    /// Exactly one of <paramref name="tenantId"/> (SaaS) / <paramref name="userId"/>
    /// (single-user) must be non-null (principal XOR).
    /// </summary>
    /// <param name="context">The control-plane context.</param>
    /// <param name="defaultPersonaName">The configured default persona handle
    /// (<c>Tamma:Agents:DefaultPersonaName</c>, e.g. <c>claude</c>).</param>
    /// <param name="tenantId">SaaS principal — set iff <paramref name="userId"/> is null.</param>
    /// <param name="userId">single-user principal — set iff <paramref name="tenantId"/> is null.</param>
    /// <param name="logger">Optional structured logger.</param>
    /// <returns><c>true</c> when a new enablement row was inserted; <c>false</c>
    /// when skipped (already present, or the default persona is unavailable).</returns>
    public static async Task<bool> SeedDefaultPersonaAsync(
        ControlPlaneDbContext context,
        string defaultPersonaName,
        Guid? tenantId,
        Guid? userId,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPersonaName);

        if ((tenantId is null) == (userId is null))
        {
            throw new ArgumentException(
                "Exactly one of tenantId / userId must be set (principal XOR).");
        }

        // The default persona must be a live (active, public) agent.
        var persona = await context.Agents.FirstOrDefaultAsync(
            a => a.Visibility == AgentVisibility.Public
                 && a.Status == AgentStatus.Active
                 && a.Name == defaultPersonaName,
            cancellationToken);

        if (persona is null)
        {
            logger?.LogWarning(
                "agent.enablement.seed_default_missing personaName={PersonaName} tenantId={TenantId} userId={UserId} — "
                + "configured default persona is not seeded/active; skipping seed (run AgentEntitySeeder first)",
                defaultPersonaName, tenantId, userId);
            return false;
        }

        // Insert-missing-only: skip if the principal already has ANY row for this
        // persona (including an explicit disable — NEVER revert it).
        var existing = await context.TenantAgentEnablements.AnyAsync(
            r => r.TenantId == tenantId && r.UserId == userId && r.AgentId == persona.Id,
            cancellationToken);

        if (existing)
        {
            logger?.LogDebug(
                "agent.enablement.seed_default_skip personaName={PersonaName} tenantId={TenantId} userId={UserId} — "
                + "an enablement row already exists (never reverts an explicit disable)",
                defaultPersonaName, tenantId, userId);
            return false;
        }

        var now = DateTime.UtcNow;
        context.TenantAgentEnablements.Add(new TenantAgentEnablement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            AgentId = persona.Id,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync(cancellationToken);

        logger?.LogInformation(
            "agent.enablement.seed_default_applied personaName={PersonaName} agentId={AgentId} tenantId={TenantId} userId={UserId}",
            defaultPersonaName, persona.Id, tenantId, userId);

        return true;
    }
}
