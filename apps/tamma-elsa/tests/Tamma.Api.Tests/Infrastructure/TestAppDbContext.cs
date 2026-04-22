using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Data;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// In-memory-friendly <see cref="TammaAppDbContext"/>. Mirrors
/// <see cref="TestDbContext"/> for repositories that have moved off
/// <see cref="TammaDbContext"/> onto the app-role context as part of
/// Story 19-6. EF InMemory rejects the mentorship jsonb / row-version
/// columns; we ignore them since prompt / sanitization / health repos
/// don't touch those entities.
///
/// <para>
/// Like <see cref="TestDbContext"/> this exposes constructors that take
/// the parent's <see cref="DbContextOptions{TammaAppDbContext}"/> so the
/// switching repositories receive a context without DI plumbing.
/// </para>
/// </summary>
public class TestAppDbContext : TammaAppDbContext
{
    public TestAppDbContext(DbContextOptions<TammaAppDbContext> options) : base(options) { }

    public TestAppDbContext(DbContextOptions<TammaAppDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mentorship entities rely on PG-specific jsonb/row-version types that
        // the InMemory provider rejects. They aren't needed for the unit tests
        // that exercise the app-role context.
        modelBuilder.Ignore<JuniorDeveloper>();
        modelBuilder.Ignore<Story>();
        modelBuilder.Ignore<MentorshipSession>();
        modelBuilder.Ignore<MentorshipEvent>();
    }
}
