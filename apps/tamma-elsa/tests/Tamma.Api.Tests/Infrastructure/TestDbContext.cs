using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Data;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// In-memory-friendly <see cref="TammaDbContext"/>. The production DbContext
/// maps <c>JsonDocument</c> properties on mentorship entities (<c>Preferences</c>,
/// <c>LearningPatterns</c>, <c>Context</c>, <c>Variables</c>, <c>EventData</c>,
/// <c>AcceptanceCriteria</c>, <c>TechnicalRequirements</c>) to Postgres <c>jsonb</c>;
/// the EF Core InMemory provider refuses these. Here we ignore the mentorship
/// entities entirely — they aren't exercised by prompt-store tests.
/// <para>
/// Both constructors accept <see cref="DbContextOptions{TammaDbContext}"/> so
/// that repositories resolved from DI can receive either this subclass or the
/// base class without conflict.
/// </para>
/// </summary>
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
