using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Story 32-1 — idempotently seeds the CP-resident <c>agents</c> /
/// <c>agent_versions</c> tables with one <b>public</b> agent per canonical
/// <c>AgentRole</c>, using the <c>tamma-&lt;role&gt;</c> handle and the shipped
/// config values (provider chain / temperature / maxTokens / maxBudgetUsd)
/// currently produced by <c>Tamma.ElsaServer/AgentSeeder.cs</c>. This is the
/// source of truth for Epic 32; it coexists with the Elsa-store seeder (which
/// populates Elsa's own <c>IAgentManager</c> store) until that is retired in a
/// later story.
///
/// <para>Insert-missing-only (mirrors <see cref="PlansSeeder"/> /
/// ConventionStoreSeeder): each agent is skipped if a public agent with the
/// same <c>(Name, Role)</c> already exists, so re-running on every startup is a
/// no-op. Owner columns are NULL (public). The seeder does NOT emit DCB events
/// — seeding system defaults is not a user-driven state transition (consistent
/// with PlansSeeder); the AGENT.CREATED.SUCCESS event family is reserved for
/// real create/publish operations via the repository/API.</para>
/// </summary>
public static class AgentEntitySeeder
{
    /// <summary>
    /// The shipped public-agent catalogue. Keyed by canonical
    /// <c>AgentRole</c> wire string; values mirror the legacy
    /// <c>AgentSeeder.GetDefaultAgents()</c> config. All share the default
    /// provider chain + budget; temperature varies per role.
    /// </summary>
    private static readonly IReadOnlyList<SeedAgent> s_seedAgents = new[]
    {
        new SeedAgent("developer", 0.4,
            "Expert software developer for code generation and fixes."),
        new SeedAgent("tester", 0.3,
            "QA engineer for test strategy and generation."),
        new SeedAgent("security", 0.2,
            "Security reviewer for vulnerabilities, secrets, and compliance."),
        new SeedAgent("devops", 0.3,
            "DevOps engineer for infrastructure, CI/CD, and deployment."),
        new SeedAgent("architect", 0.3,
            "Software architect for system design decisions."),
        new SeedAgent("product_owner", 0.4,
            "Product owner for intake, requirements, and prioritisation."),
        new SeedAgent("senior_developer", 0.2,
            "Tech lead for decomposition, code review, and mentorship."),
        new SeedAgent("tech_writer", 0.4,
            "Technical writer for documentation."),
    };

    private static readonly string[] s_providerChainNames = ["anthropic", "openai", "openrouter"];
    private const int DefaultMaxTokens = 4096;
    private const double DefaultMaxBudgetUsd = 10.0;

    /// <summary>
    /// Insert any missing public agents + their <c>Version=1</c> snapshots.
    /// Safe to call on every startup — skip-by-existing-handle makes re-runs a
    /// no-op. Returns the number of agents inserted.
    /// </summary>
    public static async Task<int> SeedAsync(
        ControlPlaneDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Existing public (Name, Role) pairs for this seed set — skip those. The
        // key must match the partial unique index on (Name, Role) so the
        // idempotency check and the DB constraint can't diverge.
        var existing = await context.Agents
            .Where(a => a.Visibility == AgentVisibility.Public)
            .Select(a => new { a.Name, a.Role })
            .ToListAsync(cancellationToken);
        var existingSet = existing
            .Select(a => (a.Name, a.Role))
            .ToHashSet();

        var now = DateTime.UtcNow;
        var inserted = 0;

        foreach (var seed in s_seedAgents)
        {
            var handle = $"tamma-{seed.Role}";
            if (existingSet.Contains((handle, seed.Role)))
            {
                continue;
            }

            var agentId = Guid.NewGuid();
            var versionId = Guid.NewGuid();

            context.Agents.Add(new Agent
            {
                Id = agentId,
                Name = handle,
                Role = seed.Role,
                Visibility = AgentVisibility.Public,
                OwnerTenantId = null,
                OwnerUserId = null,
                Status = AgentStatus.Active,
                CurrentVersionId = versionId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            context.AgentVersions.Add(new AgentVersion
            {
                Id = versionId,
                AgentId = agentId,
                Version = 1,
                ConfigJson = BuildConfigJson(seed),
                Notes = "Shipped default (Story 32-1 seed).",
                CreatedAt = now,
            });
            inserted++;
        }

        if (inserted > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return inserted;
    }

    /// <summary>
    /// Build a saved-config snapshot that validates against
    /// <c>AgentConfigValidator</c> and mirrors the shipped values: primary
    /// <c>provider</c>, full <c>providerChain</c>, <c>temperature</c>,
    /// <c>maxTokens</c>, <c>maxBudgetUsd</c>.
    /// </summary>
    private static string BuildConfigJson(SeedAgent seed)
    {
        var config = new
        {
            provider = s_providerChainNames[0],
            temperature = seed.Temperature,
            maxTokens = DefaultMaxTokens,
            maxBudgetUsd = DefaultMaxBudgetUsd,
            providerChain = s_providerChainNames
                .Select(p => new { provider = p })
                .ToArray(),
            description = seed.Description,
        };
        return JsonSerializer.Serialize(config);
    }

    private readonly record struct SeedAgent(string Role, double Temperature, string Description);
}
