using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Data;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// Wave A.5 post-merge: the separate <c>TammaAppDbContext</c> was deleted
/// when the two contexts collapsed into <see cref="ControlPlaneDbContext"/>
/// (CP tables) + <see cref="TenantDbContext"/> (per-tenant tables). This
/// type remains as a thin <see cref="ControlPlaneDbContext"/> subclass so
/// the handful of Story 19-6-era tests that still depend on the "app
/// role" context name keep compiling without touching production code.
/// New tests should instantiate <see cref="ControlPlaneDbContext"/> (CP
/// reads) or <see cref="TenantDbContext"/> (per-tenant writes) directly.
///
/// <para>EF InMemory rejects the mentorship jsonb / row-version columns;
/// we ignore them since the legacy repo tests don't touch those
/// entities.</para>
/// </summary>
public class TestAppDbContext : ControlPlaneDbContext
{
    public TestAppDbContext(DbContextOptions<ControlPlaneDbContext> options) : base(options) { }

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
