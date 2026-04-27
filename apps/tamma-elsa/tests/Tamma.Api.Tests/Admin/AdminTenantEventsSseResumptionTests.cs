using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Endpoints.Admin;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Round-2 follow-up — coverage for W3C SSE <c>Last-Event-ID</c>
/// resumption support in
/// <see cref="AdminTenantEventsSseEndpoint"/>.
///
/// <para>The endpoint must:
/// <list type="bullet">
///   <item>Start at the current high-water mark when the header is
///     absent (existing behaviour).</item>
///   <item>Resume strictly past the matched row when the header
///     parses as a Guid and the row exists for this tenant.</item>
///   <item>Fall through to the high-water mark when the header is
///     malformed, references an unknown id, or references an id
///     from a different tenant — never 400.</item>
///   <item>Reflect the resolved cursor in the
///     <c>: stream-open</c> opening comment so operators can read
///     the wire and see exactly what the server decided.</item>
/// </list>
/// </para>
///
/// <para>These tests exercise the public
/// <see cref="AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync"/>
/// helper directly + drive the full <c>StreamEvents</c> path with
/// a hand-rolled <see cref="HttpContext"/> to assert the
/// <c>: stream-open</c> wire contract end-to-end. Avoids the full
/// HTTP host so the suite stays fast.</para>
/// </summary>
[TestFixture]
public class AdminTenantEventsSseResumptionTests
{
    [Test]
    public async Task Resumption_AbsentHeader_StartsAtSequenceZero()
    {
        var tenantId = Guid.NewGuid();
        var http = BuildContext(); // no Last-Event-ID header
        var factory = new InMemoryFactory();

        await StreamUntilFirstTickAsync(tenantId, http, factory);

        var body = ReadBody(http);
        // Empty store + no header → cursor 0.
        body.Should().Contain($": stream-open tenantId={tenantId:D} cursor=0",
            "absent header must start at the high-water mark, which is 0 for an empty store");
    }

    [Test]
    public async Task Resumption_AbsentHeader_StartsAtHighWaterMark_NotZero()
    {
        // Defence-in-depth: when the store already has events for the
        // tenant, "no header" must still mean "start at the current
        // high-water mark" so existing clients don't get a flood of
        // historical rows on first connect.
        var tenantId = Guid.NewGuid();
        var http = BuildContext();
        var factory = new InMemoryFactory();
        using (var seed = await factory.CreateDbContextAsync())
        {
            seed.PlatformEvents.AddRange(
                new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "TENANT.PROVISION.STEP_STARTED",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                    SequenceNumber = 7,
                },
                new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "TENANT.PROVISION.STEP_COMPLETED",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-4),
                    SequenceNumber = 12,
                });
            await seed.SaveChangesAsync();
        }

        await StreamUntilFirstTickAsync(tenantId, http, factory);

        var body = ReadBody(http);
        body.Should().Contain($": stream-open tenantId={tenantId:D} cursor=12");
    }

    [Test]
    public async Task Resumption_HeaderMatchesEvent_StartsAfterIt()
    {
        var tenantId = Guid.NewGuid();
        var resumeFrom = Guid.NewGuid();
        var factory = new InMemoryFactory();

        using (var seed = await factory.CreateDbContextAsync())
        {
            seed.PlatformEvents.AddRange(
                new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "TENANT.PROVISION.STEP_STARTED",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-3),
                    SequenceNumber = 3,
                },
                new PlatformEvent
                {
                    Id = resumeFrom,
                    Type = "TENANT.PROVISION.STEP_COMPLETED",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-2),
                    SequenceNumber = 5,
                },
                new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "TENANT.PROVISION.STEP_COMPLETED",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                    SequenceNumber = 9,
                });
            await seed.SaveChangesAsync();
        }

        var http = BuildContext();
        http.Request.Headers["Last-Event-ID"] = resumeFrom.ToString("D");

        await StreamUntilFirstTickAsync(tenantId, http, factory);

        var body = ReadBody(http);
        // Cursor = sequence of the matched row. Subsequent ticks emit
        // anything strictly past it (sequence 5 → next emission starts
        // at seq 9).
        body.Should().Contain($": stream-open tenantId={tenantId:D} cursor=5",
            "matched row's SequenceNumber must become the resume cursor");
    }

    [Test]
    public async Task Resumption_HeaderInvalid_StartsAtSequenceZero()
    {
        // Non-Guid junk in the header (clipboard mishap, proxy
        // mangling, attacker probe). Server must NOT 400 — well-behaved
        // EventSource clients re-send the header on every reconnect.
        var tenantId = Guid.NewGuid();
        var http = BuildContext();
        http.Request.Headers["Last-Event-ID"] = "not-a-guid-at-all";
        var factory = new InMemoryFactory();

        await StreamUntilFirstTickAsync(tenantId, http, factory);

        var body = ReadBody(http);
        body.Should().Contain($": stream-open tenantId={tenantId:D} cursor=0",
            "invalid header must fall through to the high-water mark, not 400");
        // Defensively assert no error response leaked to the client.
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task Resumption_HeaderUnknownGuid_StartsAtSequenceZero()
    {
        // Well-formed Guid that doesn't match any row — happens when
        // the event store was wiped between reconnects, or when the
        // client cached a stale id past its retention horizon.
        var tenantId = Guid.NewGuid();
        var http = BuildContext();
        http.Request.Headers["Last-Event-ID"] = Guid.NewGuid().ToString("D");
        var factory = new InMemoryFactory();

        await StreamUntilFirstTickAsync(tenantId, http, factory);

        var body = ReadBody(http);
        body.Should().Contain($": stream-open tenantId={tenantId:D} cursor=0",
            "unknown Guid header must fall through to the high-water mark");
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task Resumption_HeaderForOtherTenant_StartsAtSequenceZero()
    {
        // Defence-in-depth: even though route auth gates tenantId,
        // the Guid in the resumption header MUST be matched against
        // (Id, TenantId) so a foreign id can't fast-forward this
        // stream past its own high-water mark.
        var thisTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var foreignEventId = Guid.NewGuid();
        var factory = new InMemoryFactory();

        using (var seed = await factory.CreateDbContextAsync())
        {
            // Event belongs to OTHER tenant.
            seed.PlatformEvents.Add(new PlatformEvent
            {
                Id = foreignEventId,
                Type = "TENANT.PROVISION.STEP_COMPLETED",
                TenantId = otherTenant,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                SequenceNumber = 100,
            });
            await seed.SaveChangesAsync();
        }

        var http = BuildContext();
        http.Request.Headers["Last-Event-ID"] = foreignEventId.ToString("D");

        await StreamUntilFirstTickAsync(thisTenant, http, factory);

        var body = ReadBody(http);
        // Other tenant's high seq=100 must NOT bleed in — this tenant
        // has no rows, so cursor falls through to 0.
        body.Should().Contain($": stream-open tenantId={thisTenant:D} cursor=0",
            "foreign-tenant event id must NOT leak its SequenceNumber into this stream");
        body.Should().NotContain("cursor=100",
            "cross-tenant resume oracle must be defended against by tenantId+id match");
    }

    [Test]
    public async Task OpeningComment_ReflectsActualResumeCursor()
    {
        // Pin the contract: the `: stream-open` comment must report
        // the RESOLVED cursor (post-resumption), not the literal
        // header value. Operators reading the wire need to see what
        // the server actually decided to do with the resumption hint.
        var tenantId = Guid.NewGuid();
        var resumeFrom = Guid.NewGuid();
        var factory = new InMemoryFactory();

        using (var seed = await factory.CreateDbContextAsync())
        {
            seed.PlatformEvents.Add(new PlatformEvent
            {
                Id = resumeFrom,
                Type = "TENANT.PROVISION.STEP_COMPLETED",
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                SequenceNumber = 42,
            });
            await seed.SaveChangesAsync();
        }

        var http = BuildContext();
        http.Request.Headers["Last-Event-ID"] = resumeFrom.ToString("D");

        await StreamUntilFirstTickAsync(tenantId, http, factory);

        var body = ReadBody(http);
        body.Should().Contain($": stream-open tenantId={tenantId:D} cursor=42",
            "opening comment must show the numeric cursor, NOT the Guid header value");
        body.Should().NotContain(resumeFrom.ToString("D"),
            "the stream-open comment must not echo the raw header (it carries the resolved sequence)");
    }

    /// <summary>
    /// Direct unit test of the resolver helper — covers the same
    /// matrix without needing the HTTP plumbing. Kept in addition to
    /// the wire-level assertions above so a regression in the helper
    /// surfaces with a tight, easy-to-read failure.
    /// </summary>
    [Test]
    public async Task ResolveStartingCursorAsync_Matrix()
    {
        var tenantId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var matchedId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var factory = new InMemoryFactory();

        using (var seed = await factory.CreateDbContextAsync())
        {
            seed.PlatformEvents.AddRange(
                new PlatformEvent
                {
                    Id = matchedId,
                    Type = "X",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow,
                    SequenceNumber = 17,
                },
                new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "X",
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow,
                    SequenceNumber = 99,
                },
                new PlatformEvent
                {
                    Id = foreignId,
                    Type = "X",
                    TenantId = otherTenant,
                    CreatedAt = DateTime.UtcNow,
                    SequenceNumber = 500,
                });
            await seed.SaveChangesAsync();
        }

        await using var db = await factory.CreateDbContextAsync();

        // Absent → high-water mark (99 for this tenant).
        var absent = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: null,
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        absent.Should().Be(99);

        // Empty / whitespace → high-water mark.
        var empty = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: "   ",
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        empty.Should().Be(99);

        // Invalid Guid → high-water mark.
        var invalid = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: "garbage",
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        invalid.Should().Be(99);

        // Empty Guid → high-water mark (rejected as a sentinel).
        var emptyGuid = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: Guid.Empty.ToString("D"),
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        emptyGuid.Should().Be(99);

        // Unknown well-formed Guid → high-water mark.
        var unknown = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: Guid.NewGuid().ToString("D"),
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        unknown.Should().Be(99);

        // Foreign tenant's event id → high-water mark (NOT 500).
        var foreign = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: foreignId.ToString("D"),
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        foreign.Should().Be(99,
            "foreign-tenant id must not leak its SequenceNumber");

        // Match → that row's SequenceNumber.
        var match = await AdminTenantEventsSseEndpoint.ResolveStartingCursorAsync(
            db, tenantId, lastEventIdHeader: matchedId.ToString("D"),
            NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        match.Should().Be(17);
    }

    // ── Test plumbing ─────────────────────────────────────────────

    /// <summary>
    /// Drives <see cref="AdminTenantEventsSseEndpoint.StreamEvents"/>
    /// just long enough to capture the first tick + the
    /// <c>: stream-open</c> opening comment, then cancels. Cancellation
    /// is the documented quiet-close path so the body up to that
    /// point is intact and assertable.
    /// </summary>
    private static async Task StreamUntilFirstTickAsync(
        Guid tenantId, DefaultHttpContext http, InMemoryFactory factory)
    {
        var jsonOpts = Options.Create(new JsonOptions());
        var loggerFactory = new NullLoggerFactory();
        // 500ms is well past the synchronous initial-cursor read +
        // first stream-open write, but well short of the 2s poll tick.
        // Cancellation flows through OperationCanceledException which
        // the endpoint catches as a clean break.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        http.RequestAborted = cts.Token;

        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, jsonOpts, loggerFactory, TimeProvider.System,
            http, cts.Token);
    }

    private static DefaultHttpContext BuildContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Test-local <see cref="IDbContextFactory{TContext}"/> backed by
    /// a single shared InMemory database name so seed + read see the
    /// same rows. Each test instance gets its own DB name.
    /// </summary>
    private sealed class InMemoryFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName = $"sse-resume-{Guid.NewGuid():N}";

        public ControlPlaneDbContext CreateDbContext()
        {
            var opts = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new ControlPlaneDbContext(opts);
        }

        public Task<ControlPlaneDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
