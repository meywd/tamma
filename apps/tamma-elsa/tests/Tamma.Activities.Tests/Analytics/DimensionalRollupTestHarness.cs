using Microsoft.EntityFrameworkCore;
using Tamma.Activities.Analytics;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 36-2 — shared InMemory harness for the dimensional-rollup unit tests.
/// Mirrors the Story 28-10 <c>ComputeTenantRollupActivityTests</c> fakes: each
/// tenant id maps to its own named InMemory database so the read-compute-upsert
/// cycle runs without a real Postgres. Relational-only guarantees (NULLS NOT
/// DISTINCT collision, <c>ExecuteDeleteAsync</c>) are proven by the Postgres
/// Testcontainer suite in <c>Tamma.Api.Tests</c>.
/// </summary>
internal sealed class FakeTenantDbContextFactory : Tamma.Data.Abstractions.ITenantDbContextFactory
{
    private readonly Dictionary<Guid, string> _names = new();
    private readonly List<IDisposable> _opened;

    public FakeTenantDbContextFactory(List<IDisposable> opened) => _opened = opened;

    public TenantDbContext Register(Guid tenantId)
    {
        var name = $"dim-tenant-{tenantId:N}";
        _names[tenantId] = name;
        return OpenContext(name);
    }

    public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!_names.TryGetValue(tenantId, out var name))
            throw new InvalidOperationException($"Tenant {tenantId} not reachable.");
        return new ValueTask<TenantDbContext>(OpenContext(name));
    }

    private TenantDbContext OpenContext(string name)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var ctx = new InMemoryFriendlyTenantDbContext(options);
        _opened.Add(ctx);
        return ctx;
    }
}

/// <summary>
/// InMemory-friendly <see cref="TenantDbContext"/> — drops the mentorship
/// aggregate (jsonb + rowversion columns the InMemory provider rejects). The
/// dimensional rollup only reads workflow_instances, domain_events,
/// provider_diagnostics and writes analytics_usage_* + the checkpoint.
/// </summary>
internal sealed class InMemoryFriendlyTenantDbContext : TenantDbContext
{
    public InMemoryFriendlyTenantDbContext(DbContextOptions<TenantDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<Tamma.Core.Entities.JuniorDeveloper>();
        modelBuilder.Ignore<Tamma.Core.Entities.Story>();
        modelBuilder.Ignore<Tamma.Core.Entities.MentorshipSession>();
        modelBuilder.Ignore<Tamma.Core.Entities.MentorshipEvent>();
    }
}

/// <summary>Fixed-margin test double for <see cref="IAnalyticsPricingConfig"/>.</summary>
internal sealed class FixedMarginPricing : IAnalyticsPricingConfig
{
    private readonly decimal _margin;
    public FixedMarginPricing(decimal margin) => _margin = margin;
    public decimal MarginFor(string provider) => _margin;
}
