using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Review F8 — <see cref="EfProviderSettingsRepository.UpsertAsync"/> is
/// read-then-insert; two concurrent PUTs for the same key can both miss the
/// read and both insert, turning the loser's <c>UNIQUE NULLS NOT DISTINCT</c>
/// violation (Postgres 23505) into an unhandled 500. The fix catches the
/// unique violation and retries ONCE as an update of the winner's row.
///
/// <para>Postgres itself can't run here, so the race is simulated: a derived
/// context throws the 23505-shaped <see cref="DbUpdateException"/> on the
/// FIRST insert attempt while "the concurrent winner" commits its row into
/// the same InMemory store — exactly the state a real loser observes. The
/// retry path (detach → re-query → update) then runs against the genuine
/// repository code.</para>
/// </summary>
[TestFixture]
public class ProviderSettingsRepositoryTests
{
    /// <summary>Throws the duplicate-key shape on the first ProviderSetting
    /// INSERT and invokes the "concurrent winner" callback first, so the
    /// retry has a row to find.</summary>
    private sealed class DuplicateOnFirstInsertContext : ControlPlaneDbContext
    {
        private readonly Action _concurrentWinnerCommits;
        private bool _thrown;

        public DuplicateOnFirstInsertContext(
            DbContextOptions<ControlPlaneDbContext> options, Action concurrentWinnerCommits)
            : base(options)
        {
            _concurrentWinnerCommits = concurrentWinnerCommits;
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var inserting = ChangeTracker.Entries<ProviderSetting>()
                .Any(e => e.State == EntityState.Added);
            if (inserting && !_thrown)
            {
                _thrown = true;
                _concurrentWinnerCommits();
                throw new DbUpdateException(
                    "duplicate",
                    new Npgsql.PostgresException(
                        "duplicate key value violates unique constraint "
                        + "\"ix_provider_settings_principal_provider\"",
                        "ERROR", "ERROR", "23505"));
            }
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private sealed class DelegatingFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly Func<ControlPlaneDbContext> _create;
        public DelegatingFactory(Func<ControlPlaneDbContext> create) => _create = create;
        public ControlPlaneDbContext CreateDbContext() => _create();
        public Task<ControlPlaneDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(_create());
    }

    private static DbContextOptions<ControlPlaneDbContext> Options() =>
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase($"provider-settings-repo-{Guid.NewGuid():N}")
            .Options;

    [Test]
    public async Task Upsert_InsertLosesTheUniqueRace_RetriesOnceAsUpdate()
    {
        var options = Options();
        var updatedBy = Guid.NewGuid();

        void ConcurrentWinnerCommits()
        {
            using var db = new ControlPlaneDbContext(options);
            db.ProviderSettings.Add(new ProviderSetting
            {
                Id = Guid.NewGuid(),
                Scope = "platform",
                ProviderKey = "openai",
                DefaultModel = "winners-model",
                Enabled = true,
                UpdatedAt = DateTime.UtcNow.AddSeconds(-1),
            });
            db.SaveChanges();
        }

        var repository = new EfProviderSettingsRepository(new DelegatingFactory(
            () => new DuplicateOnFirstInsertContext(options, ConcurrentWinnerCommits)));

        var row = await repository.UpsertAsync(
            null, null, "openai", "losers-model", enabled: null, updatedBy);

        // The retry converged on last-write-wins over the winner's row —
        // exactly the outcome of the requests arriving a moment apart.
        row.DefaultModel.Should().Be("losers-model");
        row.UpdatedBy.Should().Be(updatedBy);

        await using var check = new ControlPlaneDbContext(options);
        var persisted = await check.ProviderSettings
            .Where(s => s.TenantId == null && s.UserId == null && s.ProviderKey == "openai")
            .ToListAsync();
        persisted.Should().ContainSingle(
            "the 23505 loser must update the winner's row, not surface a 500 or duplicate it")
            .Which.DefaultModel.Should().Be("losers-model");
    }

    [Test]
    public async Task Upsert_NoRace_InsertThenUpdate_SingleRow()
    {
        var options = Options();
        var repository = new EfProviderSettingsRepository(new DelegatingFactory(
            () => new ControlPlaneDbContext(options)));

        await repository.UpsertAsync(null, null, "openai", "first-model", null, null);
        var updated = await repository.UpsertAsync(null, null, "openai", "second-model", null, null);

        updated.DefaultModel.Should().Be("second-model");
        await using var check = new ControlPlaneDbContext(options);
        (await check.ProviderSettings.CountAsync()).Should().Be(1);

        (await repository.DeleteAsync(null, null, "openai")).Should().BeTrue();
        (await repository.DeleteAsync(null, null, "openai")).Should().BeFalse();
    }
}
