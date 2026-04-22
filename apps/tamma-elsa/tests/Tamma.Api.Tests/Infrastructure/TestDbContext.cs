using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Data;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// In-memory-friendly <see cref="TammaDbContext"/>. Retained for tests still
/// referencing the obsolete type during the Wave A.5 transition. New test
/// code targets <see cref="TestControlPlaneDbContext"/> and
/// <see cref="TestTenantDbContextFactory"/> directly.
/// </summary>
[Obsolete("Use TestControlPlaneDbContext / TestTenantDbContextFactory for Epic 28 tests.", error: false)]
public class TestDbContext : TammaDbContext
{
    public TestDbContext(DbContextOptions<TammaDbContext> options) : base(options) { }

    public TestDbContext(DbContextOptions<TammaDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mentorship entities rely on PG-specific jsonb/row-version types that
        // the InMemory provider rejects. They aren't needed for prompt tests.
        modelBuilder.Ignore<JuniorDeveloper>();
        modelBuilder.Ignore<Story>();
        modelBuilder.Ignore<MentorshipSession>();
        modelBuilder.Ignore<MentorshipEvent>();
    }
}

/// <summary>
/// In-memory friendly <see cref="ControlPlaneDbContext"/>. Mirrors the
/// production class with EF-InMemory-hostile mentorship entities elided.
/// </summary>
public class TestControlPlaneDbContext : ControlPlaneDbContext
{
    public TestControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<JuniorDeveloper>();
        modelBuilder.Ignore<Story>();
        modelBuilder.Ignore<MentorshipSession>();
        modelBuilder.Ignore<MentorshipEvent>();
    }
}

/// <summary>
/// In-memory friendly <see cref="TenantDbContext"/>.
/// </summary>
public class TestTenantDbContext : TenantDbContext
{
    public TestTenantDbContext(DbContextOptions<TenantDbContext> options, Guid tenantId)
        : base(options, tenantId) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<JuniorDeveloper>();
        modelBuilder.Ignore<Story>();
        modelBuilder.Ignore<MentorshipSession>();
        modelBuilder.Ignore<MentorshipEvent>();
    }
}

/// <summary>
/// Test <see cref="ITenantDbContextFactory"/> that hands out new contexts
/// bound to whatever <see cref="DbContextOptions{TenantDbContext}"/> was
/// supplied at construction. Ideal for EF-InMemory / SQLite-in-tests
/// where we want a deterministic shared database.
/// </summary>
public sealed class TestTenantDbContextFactory : ITenantDbContextFactory
{
    private readonly DbContextOptions<TenantDbContext> _options;

    public TestTenantDbContextFactory(DbContextOptions<TenantDbContext> options)
    {
        _options = options;
    }

    public Task<TenantDbContext> CreateAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException(
                "Tenant id is required.", nameof(tenantId));
        return Task.FromResult<TenantDbContext>(new TestTenantDbContext(_options, tenantId));
    }
}
