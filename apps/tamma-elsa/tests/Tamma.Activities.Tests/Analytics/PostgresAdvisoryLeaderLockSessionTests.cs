using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NUnit.Framework;
using Tamma.Data.Pooling;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// 2026-07-30 advisory-lock audit — <see cref="PostgresAdvisoryLeaderLock"/>
/// is the per-hour leader-election lock used by both
/// <see cref="HourlyAnalyticsRollupScheduler"/> and
/// <c>TenantScheduledTriggerService</c>.
///
/// <para><b>The defect.</b> It opened <c>new NpgsqlConnection(cs)</c>
/// against a plain connection string — i.e. a POOLED connection — took
/// <c>pg_try_advisory_lock</c> on it, and its lease's dispose swallowed a
/// failed unlock with the comment "closing the connection releases the
/// lock either way". For a pooled connection that is false: disposal
/// returns the connector to the pool with the backend session, and the
/// hour's lock, still alive, and Npgsql defers the <c>DISCARD ALL</c> that
/// runs <c>pg_advisory_unlock_all()</c> until that connector is next USED.
/// A swallowed unlock therefore parked the hour's leader lock shut, every
/// pod read "another pod is the leader for this hour" and skipped, and
/// that hour's rollup was dispatched by nobody (the workflow infers its
/// target hour from the clock, so a skipped hour is never backfilled).</para>
///
/// <para><b>What this test can and cannot do.</b> This is a STRUCTURAL
/// pin, not a behavioural one. <see cref="PostgresAdvisoryLeaderLock"/> is
/// internal to <c>Tamma.ElsaServer</c> and therefore reachable only from
/// this project, which has no Testcontainers infrastructure; and
/// <c>Tamma.Api.Tests</c>, which does, cannot see <c>Tamma.ElsaServer</c>
/// at all. Rather than add a Postgres container to this suite (which would
/// make every run of <c>Tamma.Activities.Tests</c> require Docker and
/// change the repo's test topology), the behavioural proof for this site
/// lives in <c>Tamma.Api.Tests/Pooling/PostgresAdvisoryLockTests</c>,
/// which exercises against a real cluster the exact
/// <see cref="PostgresAdvisoryLock.TryAcquireAsync"/> call this class now
/// makes. What is pinned HERE is that the class delegates rather than
/// re-deriving its own connection handling — which is what regressed.</para>
/// </summary>
[TestFixture]
public class PostgresAdvisoryLeaderLockSessionTests
{
    [Test]
    public void The_leader_lock_owns_no_connection_of_its_own()
    {
        // Pre-fix, this class declared a nested AdvisoryLockLease holding an
        // NpgsqlConnection field — the pooled connection that could park the
        // hour's gate shut. Post-fix it holds no connection at all: the
        // session belongs to PostgresAdvisoryLock, which guarantees
        // Pooling=false. Any future author who re-inlines a connection here
        // re-introduces exactly the audited defect, and trips this test.
        var type = typeof(PostgresAdvisoryLeaderLock);

        var connectionFields = type
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Append(type)
            .SelectMany(t => t.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static))
            .Where(f => typeof(System.Data.Common.DbConnection).IsAssignableFrom(f.FieldType))
            .Select(f => $"{f.DeclaringType!.Name}.{f.Name}")
            .ToList();

        connectionFields.Should().BeEmpty(
            "the leader lock must not hold a Postgres connection itself — a lock on a "
            + "connection this class opened directly is a lock on a POOLED connection, "
            + "which survives disposal in the pool with the lock still held. The session "
            + "must come from PostgresAdvisoryLock, which opens it with Pooling=false");
    }

    [Test]
    public void The_only_lease_the_leader_lock_declares_is_the_no_database_no_op()
    {
        // The pre-fix class declared two leases: NoOpLease (no connection
        // string configured → single-pod mode) and AdvisoryLockLease (the
        // pooled one). Only the first may remain.
        var leases = typeof(PostgresAdvisoryLeaderLock)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => typeof(IAsyncDisposable).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        leases.Should().BeEquivalentTo(new[] { "NoOpLease" },
            "the real lease must be PostgresAdvisoryLockLease, whose non-pooled session "
            + "is what makes 'closing the connection releases the lock' true");
    }

    [Test]
    public async Task With_no_connection_string_it_still_elects_this_pod_as_leader()
    {
        // Unchanged semantics: a dev/unit environment with no Postgres is
        // single-pod, so the tick still dispatches. The returned lease must
        // be the no-op, and disposing it must not throw.
        var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var leaderLock = new PostgresAdvisoryLeaderLock(cfg);

        var lease = await leaderLock.TryAcquireAsync(12345L, CancellationToken.None);

        lease.Should().NotBeNull("no database means single-pod, so this pod is the leader");
        lease.Should().NotBeOfType<PostgresAdvisoryLockLease>();
        await lease!.DisposeAsync();
    }

    [Test]
    public async Task An_unreachable_cluster_throws_rather_than_reporting_leadership()
    {
        // Fail closed: a lock that could not be attempted must never read as
        // acquired, or two pods dispatch the same hour. (Unchanged by the
        // audit; pinned because the acquisition path moved.)
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=127.0.0.1;Port=1;Database=tamma;Username=tamma;Password=tamma;Timeout=2",
            }).Build();
        var leaderLock = new PostgresAdvisoryLeaderLock(cfg);

        var act = async () => await leaderLock.TryAcquireAsync(12345L, CancellationToken.None);

        await act.Should().ThrowAsync<NpgsqlException>();
    }
}
