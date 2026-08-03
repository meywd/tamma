using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-15 (Amendment 2-H) — the honest telemetry reader. Unit-tested with
/// fakes (no DB): the load-bearing risks are the PINNED source map and the
/// two-prefix merge trap, both provable without event-store plumbing. The
/// endpoint's null/no-data path is pinned by <c>ActionPolicyEndpointsTests</c>.
/// </summary>
[TestFixture]
public class ActionTelemetryReaderTests
{
    [Test]
    public void TelemetrySourceMap_IsPinned()
    {
        // Adding or removing a fire-count source must be a REVIEWED diff — a
        // future author who wires an emitter has to consciously widen this.
        ActionTelemetryReader.Sources.Keys.Should().BeEquivalentTo(new[]
        {
            "effect:git.branch.create",
            "effect:git.branch.delete",
            "effect:git.pull-request.create",
            "effect:git.merge.dev",
            "effect:git.merge.qa",
            "effect:git.merge.main",
            "effect:git.release.create",
            "effect:git.issue.patch",
            "effect:agent-dispatch.run",
        });

        // The two-prefix trap: merge carries BOTH the success and failure prefixes.
        ActionTelemetryReader.Sources["effect:git.merge.main"]
            .Should().BeEquivalentTo(new[] { "GIT.PR_MERGED.", "GIT.PR_MERGE." });
    }

    [Test]
    public async Task FireCount_SumsBothMergePrefixes()
    {
        // The merge action's fire count is GIT.PR_MERGED.* (success) PLUS
        // GIT.PR_MERGE.* (failure) — two different prefixes for one action.
        var events = new FakeEventRepository
        {
            Counts =
            {
                ["GIT.PR_MERGED."] = 3,
                ["GIT.PR_MERGE."] = 2,
            },
        };
        var reader = new ActionTelemetryReader(events, new FakeLedger());

        var result = await reader.ReadAsync(
            tenantId: null, userId: Guid.NewGuid(), new[] { "effect:git.merge.main" });

        result["effect:git.merge.main"].FireCount30d.Should().Be(5,
            "both prefixes count as a merge fire (the two-prefix trap)");
    }

    [Test]
    public async Task ZeroRows_RendersNull_NotZero()
    {
        // A source with zero rows is indistinguishable from an unwired emitter
        // (the H chicken-and-egg) → "no data", never 0.
        var reader = new ActionTelemetryReader(new FakeEventRepository(), new FakeLedger());

        var result = await reader.ReadAsync(
            tenantId: null, userId: Guid.NewGuid(), new[] { "effect:git.merge.main" });

        result["effect:git.merge.main"].FireCount30d.Should().BeNull();
    }

    [Test]
    public async Task ActionWithNoSource_HasNullFireCount()
    {
        var reader = new ActionTelemetryReader(new FakeEventRepository(), new FakeLedger());

        var result = await reader.ReadAsync(
            tenantId: null, userId: Guid.NewGuid(), new[] { "agent-action:deploy" });

        result["agent-action:deploy"].FireCount30d.Should().BeNull(
            "agent-actions have no fire-count source (the .ALLOWED volume gate)");
    }

    [Test]
    public async Task ApproveRate_ComesFromDecidedActionGrants()
    {
        var uid = Guid.NewGuid();
        var ledger = new FakeLedger
        {
            Decided =
            {
                Row("agent-action:deploy", "granted"),
                Row("agent-action:deploy", "granted"),
                Row("agent-action:deploy", "denied"),
            },
        };
        var reader = new ActionTelemetryReader(new FakeEventRepository(), ledger);

        var result = await reader.ReadAsync(null, uid, new[] { "agent-action:deploy" });

        result["agent-action:deploy"].ApproveRate30d.Should().BeApproximately(2.0 / 3, 1e-9);
    }

    private static ActionAuthorization Row(string targetKey, string state) => new()
    {
        Id = Guid.NewGuid(),
        TargetKind = "action",
        TargetKey = targetKey,
        State = state,
        RequestedAtUtc = DateTime.UtcNow,
        DecidedAtUtc = DateTime.UtcNow,
    };

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeEventRepository : IEventRepository
    {
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

        public Task<int> CountByTypePrefixSinceAsync(
            Guid? tenantId, string typePrefix, DateTime sinceUtc) =>
            Task.FromResult(Counts.TryGetValue(typePrefix, out var c) ? c : 0);

        public Task<DomainEvent> AppendAsync(DomainEvent evt) => Task.FromResult(evt);
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? t, string? ty, int? i, int l) =>
            Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid t, string ty) =>
            Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid t) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? t, string? ty, int? i, int l, int o) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid t, string? p, int l, int o) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }

    private sealed class FakeLedger : IActionAuthorizationLedger
    {
        public List<ActionAuthorization> Decided { get; } = new();

        public Task<IReadOnlyList<ActionAuthorization>> ListDecidedSinceAsync(
            Guid? tenantId, Guid? userId, DateTime sinceUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActionAuthorization>>(Decided);

        public Task<ActionAuthorization> RequestAsync(
            Guid? t, Guid? u, string c, string tk, string key, string? r, int? lvl,
            TimeSpan? ttl = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ActionAuthorization?> TryConsumeAsync(
            Guid? t, Guid? u, string c, string wire, CancellationToken ct = default) =>
            Task.FromResult<ActionAuthorization?>(null);
        public Task<ActionAuthorization?> DecideAsync(
            Guid? t, Guid? u, Guid id, bool g, Guid by, string? r, CancellationToken ct = default) =>
            Task.FromResult<ActionAuthorization?>(null);
    }
}
