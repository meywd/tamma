using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Reveal;

/// <summary>
/// Story 29-3 dedicated <see cref="DbContext"/> for the
/// <c>secret_reveal_tokens</c> table. Kept separate from the Story 29-2
/// <see cref="Postgres.SecretsDbContext"/> so this story does not
/// touch 29-2 internals (the hard scope rule forbids edits there). In
/// production both contexts ride on the same physical database (the
/// control-plane database for platform-scoped secrets; the per-tenant
/// database for tenant-scoped secrets) via independent migration
/// history tables.
///
/// <para>Migration history table:
/// <c>__SecretRevealMigrationsHistory</c> — separate from 29-2's
/// <c>__SecretStoreMigrationsHistory</c> so the reveal-token schema
/// can roll forward / roll back without disturbing the underlying
/// secret-cabinet schema.</para>
///
/// <para>The context is <em>read-mostly</em>: Story 29-3 inserts one
/// row per create / rotate (low volume) and flips status on consume /
/// sweep. No JSONB, no cascade deletes — the row references
/// <see cref="SecretRow.Id"/> logically via <c>SecretId</c> but we do
/// NOT declare the FK here so the migration does not need a
/// <see cref="SecretsDbContext"/> cross-reference.</para>
/// </summary>
public class SecretRevealDbContext : DbContext
{
    public SecretRevealDbContext(DbContextOptions<SecretRevealDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Reveal-token rows. One row per reveal token issued by
    /// <see cref="SecretRevealService.IssueAsync"/>.
    /// </summary>
    public DbSet<SecretRevealTokenRow> RevealTokens => Set<SecretRevealTokenRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SecretRevealTokenRow>(entity =>
        {
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Partial index on (status='unused', expires_at) so the
            // 30-second sweep query ("WHERE status='unused' AND
            // expires_at < NOW()") stays cheap as the table grows.
            // EF Core does not express partial indexes via
            // DataAnnotations, so this is pinned inside the migration
            // body via raw SQL. The non-partial Status/ExpiresAt index
            // declared via [Index] gives us a fallback for ORMs that
            // ignore the partial hint.
        });
    }
}
