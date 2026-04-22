using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Data;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// In-memory friendly <see cref="ControlPlaneDbContext"/>. Mirrors the
/// production class with EF-InMemory-hostile mentorship entities elided
/// (<c>JsonDocument</c>/<c>jsonb</c>/row-version types the InMemory
/// provider cannot materialise).
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

    public ValueTask<TenantDbContext> CreateAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException(
                "Tenant id is required.", nameof(tenantId));
        return ValueTask.FromResult<TenantDbContext>(new TestTenantDbContext(_options, tenantId));
    }
}
