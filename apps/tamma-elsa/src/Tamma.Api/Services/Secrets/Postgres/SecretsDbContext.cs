using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Postgres;

/// <summary>
/// Story 29-2 standalone <see cref="DbContext"/> that owns the
/// secret-cabinet schema (<c>secrets</c> + <c>secret_versions</c>).
/// Designed to ride on either the control-plane or a per-tenant
/// Postgres database without coupling to Epic 28's
/// <see cref="Tamma.Data.ControlPlaneDbContext"/> /
/// <see cref="Tamma.Data.TenantDbContext"/> — neither of those gets
/// new entity registrations from this story (the hard scope rule
/// forbids edits there).
///
/// <para>The connection string is supplied at construction time by
/// <see cref="SecretsDbContextFactory"/>; the factory routes
/// platform-scoped operations to the control-plane connection and
/// tenant-scoped operations to the per-tenant connection resolved
/// via Story 28-4's <see cref="Tamma.Data.Abstractions.ITenantConnectionResolver"/>.</para>
///
/// <para>Migration history table:
/// <c>__SecretStoreMigrationsHistory</c> — separate from
/// <c>__ControlPlaneMigrationsHistory</c> and
/// <c>__TenantMigrationsHistory</c> so the secrets schema can roll
/// forward independently of Epic 28's table set. Both control-plane
/// and per-tenant databases get the same schema (one set of
/// secret-cabinet tables per database) — the discriminator is
/// implicit in the connection.</para>
///
/// <para>Schema column types + indexes are pinned via
/// DataAnnotations on <see cref="SecretRow"/> /
/// <see cref="SecretVersionRow"/>. The CHECK constraints on
/// <c>secrets.scope</c> are added in <see cref="OnModelCreating"/>
/// here so the migration emits them — DataAnnotations don't have a
/// CHECK-constraint primitive in EF Core 8.</para>
/// </summary>
public class SecretsDbContext : DbContext
{
    public SecretsDbContext(DbContextOptions<SecretsDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Secret-cabinet metadata rows. One row per managed secret.
    /// </summary>
    public DbSet<SecretRow> Secrets => Set<SecretRow>();

    /// <summary>
    /// Secret-cabinet version rows. One row per minted version of a
    /// <see cref="SecretRow"/>; the
    /// <see cref="SecretVersionRow.Ciphertext"/> column carries the
    /// AES-256-GCM envelope produced by <see cref="SecretEnvelope"/>.
    /// </summary>
    public DbSet<SecretVersionRow> SecretVersions => Set<SecretVersionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SecretRow>(entity =>
        {
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.ConsumerRefsJson)
                .HasDefaultValueSql("'[]'::jsonb");
            entity.Property(e => e.RotationScheduleJson)
                .HasDefaultValueSql("'{\"Kind\":\"None\"}'::jsonb");

            // Story 29-1 (review fix) — unique per (Scope, TenantId, Name).
            // Including TenantId lets two tenants each hold a same-named
            // tenant-scoped secret (JIRA / email BYOK); the prior (Scope,
            // Name) index collided them cross-tenant. NULLS NOT DISTINCT
            // (PG15+; production runs PG17 — same pattern as prompt_overrides
            // / conventions) collapses the NULL TenantId that all
            // platform-scope rows share, so platform-scope name uniqueness is
            // still enforced (two platform rows with the same Name collide).
            entity.HasIndex(e => new { e.Scope, e.TenantId, e.Name })
                .IsUnique()
                .AreNullsDistinct(false);

            // Application-level discriminator. The two databases
            // (control-plane vs tenant) carry rows of opposite scope
            // by routing convention; the CHECK isn't pinned here
            // because the same DbContext schema is applied to both.
            // The repository layer enforces the right scope at write
            // time and raises a deterministic error on a mismatch.
        });

        modelBuilder.Entity<SecretVersionRow>(entity =>
        {
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Cascade-delete versions when their parent secret is
            // dropped. Production code never DELETEs a secret row
            // (revoke = scrub + retain) but keeping the FK consistent
            // means a future tenant-purge story doesn't have to
            // hand-walk the versions table.
            entity.HasOne<SecretRow>()
                .WithMany()
                .HasForeignKey(e => e.SecretId)
                .OnDelete(DeleteBehavior.Cascade);

            // Story 29-6 (review fix) — at most ONE in-flight rotation per
            // secret, enforced in SQL. A partial unique index on SecretId
            // WHERE Status = 'pending' closes the TryBeginRotationAsync
            // TOCTOU: two concurrent triggers that both observe "no pending"
            // can no longer both mint — the loser's INSERT raises a unique
            // violation, which the gateway maps to a clean rotation-rejected
            // result (no silent plaintext collapse / double-push).
            entity.HasIndex(e => e.SecretId)
                .HasDatabaseName("UX_secret_versions_OnePendingPerSecret")
                .HasFilter("\"Status\" = 'pending'")
                .IsUnique();
        });
    }
}
