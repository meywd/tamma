using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Engine.Replay;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Unit coverage for <see cref="ReplayService"/>'s read-endpoint hardening, driven by a
/// fake <see cref="IEventRepository"/> (no DB):
/// <list type="bullet">
///   <item><b>Fix C</b> — the bounded read's <c>Truncated</c> flag flows through to
///     <see cref="ReplayResult.Truncated"/> (signalled, not swallowed).</item>
///   <item><b>Fix B</b> — a <c>from</c> that resolves AFTER <c>upTo</c> throws
///     <see cref="ReplayRangeException"/> (which the endpoint maps to 400), rather than
///     returning a 200 with a meaningless empty delta.</item>
/// </list>
/// </summary>
[TestFixture]
public class ReplayServiceTruncationTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ReplayService NewService(IReadOnlyList<DomainEvent> events, bool truncated)
        => new(new FakeEventRepo(events, truncated), NullLogger<ReplayService>.Instance);

    private static DomainEvent Evt(long seq, string type) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        TenantId = Guid.NewGuid(),
        Tags = "{}",
        Metadata = "{}",
        Data = "{}",
        CreatedAt = Base.AddSeconds(seq),
        SequenceNumber = seq,
    };

    [Test]
    public async Task Truncated_FlowsThroughToResult()
    {
        var svc = NewService(new[] { Evt(1, "A"), Evt(2, "B") }, truncated: true);

        var result = await svc.ReplayAsync(Guid.NewGuid(), "run", null, null, null);

        result.Should().NotBeNull();
        result!.Truncated.Should().BeTrue("the bounded read reported truncation");
    }

    [Test]
    public async Task NotTruncated_ResultFlagFalse()
    {
        var svc = NewService(new[] { Evt(1, "A"), Evt(2, "B") }, truncated: false);

        var result = await svc.ReplayAsync(Guid.NewGuid(), "run", null, null, null);

        result!.Truncated.Should().BeFalse();
    }

    [Test]
    public async Task FromAfterUpTo_ThrowsReplayRangeException()
    {
        var svc = NewService(new[] { Evt(1, "A"), Evt(2, "B"), Evt(3, "C") }, truncated: false);

        // upTo = seq 1 (slice of 1) but from = seq 3 (slice of 3) → from is after upTo.
        var act = async () => await svc.ReplayAsync(
            Guid.NewGuid(), "run", upToSequence: 1, upToTimestamp: null, fromSequence: 3);

        await act.Should().ThrowAsync<ReplayRangeException>();
    }

    [Test]
    public async Task FromAtUpTo_DoesNotThrow_EmptyDelta()
    {
        var svc = NewService(new[] { Evt(1, "A"), Evt(2, "B"), Evt(3, "C") }, truncated: false);

        var result = await svc.ReplayAsync(
            Guid.NewGuid(), "run", upToSequence: 3, upToTimestamp: null, fromSequence: 3);

        result!.Delta.Should().NotBeNull();
        result.Delta!.AddedEventCount.Should().Be(0);
    }

    private sealed class FakeEventRepo(IReadOnlyList<DomainEvent> events, bool truncated) : IEventRepository
    {
        public Task<(IReadOnlyList<DomainEvent> Events, bool Truncated)> ListByCorrelationIdAsync(
            Guid tenantId, string correlationId, int maxEvents)
            => Task.FromResult((events, truncated));

        // Non-default interface members — unused by ReplayService, stubbed out.
        public Task<DomainEvent> AppendAsync(DomainEvent evt) => throw new NotSupportedException();
        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => throw new NotSupportedException();
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }
}
