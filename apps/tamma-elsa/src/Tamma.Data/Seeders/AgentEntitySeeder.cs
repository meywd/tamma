using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Data.Seeders;

/// <summary>
/// Story 32-15 — idempotently seeds the CP-resident <c>agents</c> /
/// <c>agent_versions</c> tables with the named, cross-role public PERSONAS
/// (<c>claude</c>/<c>gemini</c>/<c>codegpt</c>). Each persona is
/// <c>Visibility='public'</c>, <c>Role=NULL</c> (cross-role — usable for ANY
/// role), owners NULL, with a <c>Version=1</c> <see cref="AgentVersion"/> whose
/// <c>ConfigJson</c> pins an explicit <c>provider</c> AND <c>model</c> and
/// carries NO prompts (personas are prompt-free — the role/system prompt is the
/// Epic 27 prompt store's job, keyed <c>(principal, role, action)</c>).
///
/// <para><b>Amends the shipped 32-1 seeder.</b> 32-1 seeded one
/// <c>tamma-&lt;role&gt;</c> public agent per role on a single Anthropic provider
/// chain with <c>Role</c> as identity. This rewrite stops producing those rows
/// and produces the named personas instead; any pre-existing
/// <c>tamma-&lt;role&gt;</c> public rows are ARCHIVED (never destructive-deleted —
/// versions are immutable history) so default-persona resolution + enablement
/// (32-16) operate over the named personas only (AC11).</para>
///
/// <para><b>Insert-missing-only, keyed by Name</b> (mirrors
/// <see cref="ProviderPricingSeeder"/> / <see cref="PlansSeeder"/>): a persona is
/// skipped if a public agent with the same <c>Name</c> already exists, so a
/// re-run is a no-op and NEVER reverts an admin edit to an existing persona's
/// config/version (AC4). Deterministic UUIDv7-shaped ids keep FK targets stable
/// across environments.</para>
///
/// <para><b>Cost-basis guard (AC3/AC14).</b> Each persona's <c>(provider,
/// model)</c> must be priceable — backed by Story 34-11's
/// <c>provider_model_prices</c>. The seeder checks for an <c>active</c> price row
/// before writing; an unpriceable persona is WARN-logged and skipped (no
/// half-seeded row). This seeder lives in <c>Tamma.Data</c> and cannot reference
/// the <c>Tamma.Api</c> <c>IProviderPricingService</c>, so it queries the price
/// table directly — the in-data equivalent of <c>IsKnown(provider, model)</c>.
/// It therefore MUST run AFTER <see cref="ProviderPricingSeeder"/> at startup.</para>
///
/// <para><b>DCB events on real writes only (AC12).</b> When an
/// <see cref="IPlatformEventRepository"/> is supplied, the seeder emits
/// <c>AGENT.CREATED.SUCCESS</c> (which doubles as the version-published signal
/// for the inline Version=1) per newly-created persona and
/// <c>AGENT.ARCHIVED.SUCCESS</c> per legacy row archived — never a "lie" event
/// for a skipped (already-existing) persona. Persona definitions are
/// platform-global, so events land in the control-plane <c>platform_events</c>
/// store with <c>TenantId = NULL</c> (platform feed) — written via the platform
/// event repository directly, NOT the tenant-routing <see cref="IEventRepository"/>
/// (which at startup-seed time would try to resolve a non-existent tenant DB).
/// Never logs raw <c>ConfigJson</c> — configs carry <c>provider</c>+<c>model</c>,
/// never a key.</para>
/// </summary>
public static class AgentEntitySeeder
{
    /// <summary>DNS-style namespace for deterministic seed ids. NEVER change.</summary>
    private const string IdNamespace = "tamma.persona.v1";

    private const string WorkflowVersion = "1.0.0";
    private const int DefaultMaxTokens = 4096;
    private const double DefaultMaxBudgetUsd = 10.0;

    /// <summary>
    /// The named, cross-role public personas. Each pins an explicit
    /// <c>provider</c>+<c>model</c> whose pair MUST be priceable under Story
    /// 34-11 (the seeded price book). The chosen models are present in the
    /// shipped price book:
    /// <list type="bullet">
    ///   <item><c>claude</c> → anthropic / claude-sonnet-4-20250514</item>
    ///   <item><c>gemini</c> → google / gemini-1.5-pro (the priced Gemini pro
    ///     SKU — gemini-2.5-pro is NOT in the 34-11 book)</item>
    ///   <item><c>codegpt</c> → openai / gpt-4o</item>
    /// </list>
    /// </summary>
    private static readonly IReadOnlyList<PersonaSeed> s_personas = new[]
    {
        new PersonaSeed("claude", "anthropic", "claude-sonnet-4-20250514", 0.3,
            "Anthropic Claude persona — strong general-purpose coding agent."),
        new PersonaSeed("gemini", "google", "gemini-1.5-pro", 0.3,
            "Google Gemini persona — large-context multimodal coding agent."),
        new PersonaSeed("codegpt", "openai", "gpt-4o", 0.3,
            "OpenAI GPT persona — fast general-purpose coding agent."),
    };

    /// <summary>
    /// Insert any missing named cross-role personas + their <c>Version=1</c>
    /// snapshots, and archive any legacy <c>tamma-&lt;role&gt;</c> public rows.
    /// Safe to call on every startup — skip-by-existing-name makes re-runs a
    /// no-op. Returns the number of personas inserted.
    /// </summary>
    /// <param name="context">The control-plane context.</param>
    /// <param name="events">
    /// Optional platform DCB event sink. When supplied,
    /// <c>AGENT.CREATED.SUCCESS</c> / <c>AGENT.ARCHIVED.SUCCESS</c> are emitted to
    /// the platform feed on real state transitions (AC12). Tests may pass null
    /// when they don't assert on events.
    /// </param>
    /// <param name="logger">Optional structured logger (AC14).</param>
    public static async Task<int> SeedAsync(
        ControlPlaneDbContext context,
        IPlatformEventRepository? events = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = DateTime.UtcNow;

        // Existing public persona handles — skip those (insert-missing-only
        // keyed by Name alone, matching the IX_agents_public_name index). Never
        // reverts an admin-edited persona (AC4).
        var existingPublicNames = await context.Agents
            .Where(a => a.Visibility == AgentVisibility.Public)
            .Select(a => a.Name)
            .ToListAsync(cancellationToken);
        var existingSet = existingPublicNames.ToHashSet(StringComparer.Ordinal);

        // Active priced (provider, model) pairs (Story 34-11). Case-insensitive
        // on both keys — the in-data equivalent of IProviderPricingService.IsKnown.
        var pricedPairs = await context.ProviderModelPrices
            .Where(p => p.Status == "active")
            .Select(p => new { p.ProviderKey, p.Model })
            .ToListAsync(cancellationToken);
        var pricedSet = pricedPairs
            .Select(p => (p.ProviderKey, p.Model))
            .ToHashSet(ProviderModelComparer.Instance);

        var created = new List<(Agent Agent, int Version)>();

        foreach (var persona in s_personas)
        {
            if (existingSet.Contains(persona.Name))
            {
                continue; // skip-by-existing-name — no event (AC4/AC12)
            }

            // Cost-basis guard (AC3/AC14) — refuse to seed an unpriceable
            // persona; a half-seeded row that can't meter is worse than a gap.
            if (!pricedSet.Contains((persona.Provider, persona.Model)))
            {
                logger?.LogWarning(
                    "agent.persona.unpriceable personaName={PersonaName} provider={Provider} model={Model} — "
                    + "no active price row (Story 34-11); skipping write (run ProviderPricingSeeder first)",
                    persona.Name, persona.Provider, persona.Model);
                continue;
            }

            var agentId = DeterministicId($"persona:{persona.Name}");
            var versionId = DeterministicId($"persona-version:{persona.Name}:1");

            var configJson = BuildConfigJson(persona);

            var agent = new Agent
            {
                Id = agentId,
                Name = persona.Name,
                Role = null,                          // cross-role persona (AC1/AC3)
                Visibility = AgentVisibility.Public,
                OwnerTenantId = null,
                OwnerUserId = null,
                Status = AgentStatus.Active,
                CurrentVersionId = versionId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.Agents.Add(agent);
            context.AgentVersions.Add(new AgentVersion
            {
                Id = versionId,
                AgentId = agentId,
                Version = 1,
                ConfigJson = configJson,
                Notes = "Shipped persona (Story 32-15 seed).",
                CreatedAt = now,
            });

            logger?.LogInformation(
                "agent.persona.created personaName={PersonaName} agentId={AgentId} provider={Provider} model={Model}",
                persona.Name, agentId, persona.Provider, persona.Model);

            created.Add((agent, 1));
        }

        // Legacy disposition (AC11) — archive any pre-existing tamma-<role>
        // public rows so default resolution operates over the named personas.
        // Never destructive-delete (immutable history); idempotent (skip rows
        // already Archived → no second event).
        var legacy = await context.Agents
            .Where(a => a.Visibility == AgentVisibility.Public
                        && a.Status == AgentStatus.Active
                        && a.Name.StartsWith("tamma-"))
            .ToListAsync(cancellationToken);
        var archived = new List<Agent>();
        foreach (var row in legacy)
        {
            row.Status = AgentStatus.Archived;
            row.UpdatedAt = now;
            archived.Add(row);
        }

        if (created.Count > 0 || archived.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        // Emit DCB events only after a real, committed state transition (AC12).
        if (events is not null)
        {
            foreach (var (agent, version) in created)
            {
                var persona = s_personas.First(p => p.Name == agent.Name);
                await AppendCreatedEventAsync(events, agent, version, persona);
            }
            foreach (var row in archived)
            {
                await AppendArchivedEventAsync(events, row);
            }
        }

        if (archived.Count > 0)
        {
            logger?.LogInformation(
                "agent.persona.legacy_archived disposition=archived count={Count}",
                archived.Count);
        }

        logger?.LogInformation(
            "agent.persona.seed_summary created={Created} skipped={Skipped} personas={Personas}",
            created.Count,
            s_personas.Count - created.Count,
            string.Join(",", s_personas.Select(p => p.Name)));

        return created.Count;
    }

    /// <summary>
    /// Build a persona saved-config snapshot: explicit <c>provider</c> + explicit
    /// <c>model</c>, params — and NO <c>prompts</c> block (personas are
    /// prompt-free; the role/system prompt comes from Epic 27). Validates against
    /// <c>AgentConfigValidator</c> (provider regex, ranges, ReDoS guard).
    /// </summary>
    private static string BuildConfigJson(PersonaSeed persona)
    {
        var config = new
        {
            provider = persona.Provider,
            model = persona.Model,
            temperature = persona.Temperature,
            maxTokens = DefaultMaxTokens,
            maxBudgetUsd = DefaultMaxBudgetUsd,
            description = persona.Description,
        };
        return JsonSerializer.Serialize(config);
    }

    private static async Task AppendCreatedEventAsync(
        IPlatformEventRepository events, Agent agent, int version, PersonaSeed persona)
    {
        var tags = new Dictionary<string, object?>
        {
            ["agentId"] = agent.Id.ToString(),
            ["version"] = version,
            ["visibility"] = "public",
            ["role"] = null,                      // cross-role persona (AC12)
            ["personaName"] = agent.Name,
            ["provider"] = persona.Provider,
            ["model"] = persona.Model,
            ["mode"] = "platform",
        };

        await events.AppendAsync(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "AGENT.CREATED.SUCCESS",
            TenantId = null,                      // platform feed (AC12)
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = WorkflowVersion,
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["version"] = version,
            }),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static async Task AppendArchivedEventAsync(IPlatformEventRepository events, Agent agent)
    {
        var tags = new Dictionary<string, object?>
        {
            ["agentId"] = agent.Id.ToString(),
            ["visibility"] = "public",
            ["role"] = agent.Role,
            ["personaName"] = agent.Name,
            ["mode"] = "platform",
        };

        await events.AppendAsync(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "AGENT.ARCHIVED.SUCCESS",
            TenantId = null,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = WorkflowVersion,
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>()),
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Derive a stable, deterministic UUIDv7-shaped GUID from a name. SHA-256 of
    /// <c>"{namespace}:{name}"</c>, version nibble forced to 7, RFC-4122 variant.
    /// Mirrors <see cref="ProviderPricingSeeder.DeterministicId"/>.
    /// </summary>
    public static Guid DeterministicId(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{IdNamespace}:{name}"));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x70);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid, bigEndian: true);
    }

    private readonly record struct PersonaSeed(
        string Name, string Provider, string Model, double Temperature, string Description);

    /// <summary>Case-insensitive (provider, model) tuple comparer — the in-data
    /// equivalent of the price service's case-insensitive IsKnown lookup.</summary>
    private sealed class ProviderModelComparer : IEqualityComparer<(string Provider, string Model)>
    {
        public static readonly ProviderModelComparer Instance = new();

        public bool Equals((string Provider, string Model) x, (string Provider, string Model) y)
            => string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Provider, string Model) obj)
            => HashCode.Combine(
                obj.Provider.ToLowerInvariant(),
                obj.Model.ToLowerInvariant());
    }
}
