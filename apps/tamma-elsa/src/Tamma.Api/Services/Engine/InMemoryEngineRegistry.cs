using System.Collections.Concurrent;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// In-memory <see cref="IEngineRegistry"/>. Until a real <c>TammaEngine</c>
/// abstraction is ported (finding 012), this implementation derives a
/// synthetic engine list from the workflow/event store: one engine row per
/// tenant that has at least one workflow definition synced. State is
/// <c>idle</c> / <c>running</c> based on whether any instance is currently
/// in <c>running</c> status.
///
/// <para>This is intentionally minimal. The TS registry tracked live
/// engine processes; the C# version reports what the data layer can see.
/// Once <c>TammaEngine</c> ports, swap this for the real
/// register/dispose-managed map.</para>
/// </summary>
public sealed class InMemoryEngineRegistry : IEngineRegistry
{
    // For now no engines explicitly register — the registry materialises
    // synthetic entries from the data layer at query time.
    private readonly ConcurrentDictionary<string, EngineInfo> _registered = new();
    private readonly IServiceProvider _services;

    public InMemoryEngineRegistry(IServiceProvider services)
    {
        _services = services;
    }

    public int Count => _registered.Count;

    public async Task<IReadOnlyList<EngineInfo>> ListAsync(Guid? tenantId, CancellationToken ct = default)
    {
        // Start from the explicitly-registered engines (none today).
        var explicitEntries = tenantId.HasValue
            ? _registered.Values.Where(e => e.TenantId == tenantId.Value).ToList()
            : _registered.Values.ToList();

        // Augment with a synthetic per-tenant entry derived from workflow data
        // so the dashboard tile is not blank in the absence of a real
        // TammaEngine abstraction. One synthetic engine per tenant that
        // has any workflow activity at all.
        try
        {
            using var scope = _services.CreateScope();
            var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();

            // Build an aggregate by sampling recent instances. Cheap because
            // the dashboard tile expects single-digit cardinality.
            var (instances, _) = await workflows.ListInstancesAsync(null, tenantId, 1, 50);
            var byTenant = instances
                .Where(i => i.TenantId.HasValue)
                .GroupBy(i => i.TenantId!.Value);

            foreach (var grp in byTenant)
            {
                var anyRunning = grp.Any(i => string.Equals(i.Status, "running", StringComparison.OrdinalIgnoreCase));
                var recent = await events.QueryAsync(grp.Key, null, null, 1);
                var lastEventAt = recent.FirstOrDefault()?.CreatedAt;
                explicitEntries.Add(new EngineInfo(
                    Id: $"engine-{grp.Key:N}",
                    State: anyRunning ? "running" : "idle",
                    Stats: new EngineStats(grp.Count(), lastEventAt),
                    TenantId: grp.Key));
            }
        }
        catch
        {
            // Synthetic enumeration is best-effort — never let a transient DB
            // hiccup take down the dashboard /engines tile.
        }

        return explicitEntries;
    }
}
