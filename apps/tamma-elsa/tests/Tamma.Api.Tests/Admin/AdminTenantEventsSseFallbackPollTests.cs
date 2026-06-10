using System.Text;
using System.Text.Json;
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
/// Story 28-11 AC3 — coverage for the <c>?fallback=poll</c> long-poll
/// mode of <see cref="AdminTenantEventsSseEndpoint"/>. The fallback must
/// return a one-shot JSON snapshot (NOT an event-stream) so dashboards
/// behind proxies that buffer <c>text/event-stream</c> still work:
/// <list type="bullet">
///   <item>No <c>Last-Event-ID</c> → recent events, chronological.</item>
///   <item><c>Last-Event-ID</c> present → only rows past that cursor.</item>
///   <item>Same M4 tag scrub + tenant scoping as the stream.</item>
/// </list>
/// </summary>
[TestFixture]
public class AdminTenantEventsSseFallbackPollTests
{
    [Test]
    public async Task FallbackPoll_NoHeader_ReturnsRecentEvents_AsJson()
    {
        var tenantId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var factory = new InMemoryFactory();
        await SeedAsync(factory, tenantId,
            (Guid.NewGuid(), "A", 3),
            (Guid.NewGuid(), "B", 5),
            (newestId, "C", 9));

        var http = BuildContext(fallbackPoll: true);
        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, JsonOpts(), new NullLoggerFactory(),
            TimeProvider.System, http, CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.ContentType.Should().StartWith("application/json");

        using var doc = JsonDocument.Parse(ReadBody(http));
        var root = doc.RootElement;
        var events = root.GetProperty("events");
        events.GetArrayLength().Should().Be(3);
        // Chronological — matches the stream's frame order.
        events[0].GetProperty("type").GetString().Should().Be("A");
        events[2].GetProperty("type").GetString().Should().Be("C");
        // Resume token is the newest row's Guid Id — echoed back via
        // Last-Event-ID, the SAME token the SSE stream uses.
        root.GetProperty("nextEventId").GetGuid().Should().Be(newestId);
        root.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task FallbackPoll_WithLastEventId_ReturnsOnlyNewer()
    {
        var tenantId = Guid.NewGuid();
        var cursorRow = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var factory = new InMemoryFactory();
        await SeedAsync(factory, tenantId,
            (Guid.NewGuid(), "A", 3),
            (cursorRow, "B", 5),
            (newestId, "C", 9));

        var http = BuildContext(fallbackPoll: true);
        http.Request.Headers["Last-Event-ID"] = cursorRow.ToString("D");

        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, JsonOpts(), new NullLoggerFactory(),
            TimeProvider.System, http, CancellationToken.None);

        using var doc = JsonDocument.Parse(ReadBody(http));
        var events = doc.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(1, "only rows strictly past seq 5 are new");
        events[0].GetProperty("type").GetString().Should().Be("C");
        // The returned token round-trips: it's the newest row's Guid Id,
        // a valid Last-Event-ID for the next poll.
        doc.RootElement.GetProperty("nextEventId").GetGuid().Should().Be(newestId);
    }

    [Test]
    public async Task FallbackPoll_ResumeWithNoNewRows_EchoesPriorCursor()
    {
        // Empty delta must not lose the client's place: nextEventId echoes
        // the cursor the client sent so the next poll resumes correctly.
        var tenantId = Guid.NewGuid();
        var cursorRow = Guid.NewGuid();
        var factory = new InMemoryFactory();
        await SeedAsync(factory, tenantId, (cursorRow, "ONLY", 5));

        var http = BuildContext(fallbackPoll: true);
        http.Request.Headers["Last-Event-ID"] = cursorRow.ToString("D");

        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, JsonOpts(), new NullLoggerFactory(),
            TimeProvider.System, http, CancellationToken.None);

        using var doc = JsonDocument.Parse(ReadBody(http));
        doc.RootElement.GetProperty("events").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("nextEventId").GetGuid().Should().Be(cursorRow,
            "an empty delta echoes the client's prior cursor so it keeps its place");
    }

    [Test]
    public async Task FallbackPoll_EmptyStore_ReturnsEmptyArray_NullCursor()
    {
        var tenantId = Guid.NewGuid();
        var factory = new InMemoryFactory();

        var http = BuildContext(fallbackPoll: true);
        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, JsonOpts(), new NullLoggerFactory(),
            TimeProvider.System, http, CancellationToken.None);

        using var doc = JsonDocument.Parse(ReadBody(http));
        doc.RootElement.GetProperty("events").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("nextEventId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task FallbackPoll_ScrubsDisallowedTags()
    {
        var tenantId = Guid.NewGuid();
        var factory = new InMemoryFactory();
        using (var seed = await factory.CreateDbContextAsync())
        {
            seed.PlatformEvents.Add(new PlatformEvent
            {
                Id = Guid.NewGuid(),
                Type = "TENANT.PROVISION.STEP_COMPLETED",
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow,
                SequenceNumber = 1,
                Tags = "{\"step\":\"create-role\",\"apiKey\":\"sk-leaked\"}",
                Data = "{\"secret\":\"should-not-appear\"}",
            });
            await seed.SaveChangesAsync();
        }

        var http = BuildContext(fallbackPoll: true);
        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, JsonOpts(), new NullLoggerFactory(),
            TimeProvider.System, http, CancellationToken.None);

        var body = ReadBody(http);
        body.Should().Contain("create-role", "allowlisted 'step' tag survives");
        body.Should().NotContain("sk-leaked", "non-allowlisted tag must be scrubbed");
        body.Should().NotContain("should-not-appear", "raw Data payload is never carried");
    }

    [Test]
    public async Task FallbackPoll_DoesNotLeakOtherTenants()
    {
        var tenantId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var factory = new InMemoryFactory();
        await SeedAsync(factory, tenantId, (Guid.NewGuid(), "MINE", 4));
        await SeedAsync(factory, other, (Guid.NewGuid(), "THEIRS", 7));

        var http = BuildContext(fallbackPoll: true);
        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, JsonOpts(), new NullLoggerFactory(),
            TimeProvider.System, http, CancellationToken.None);

        var body = ReadBody(http);
        body.Should().Contain("MINE");
        body.Should().NotContain("THEIRS");
    }

    // ── Test plumbing ─────────────────────────────────────────────

    private static IOptions<JsonOptions> JsonOpts() => Options.Create(new JsonOptions());

    private static DefaultHttpContext BuildContext(bool fallbackPoll)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        if (fallbackPoll)
        {
            ctx.Request.Query = new QueryCollection(
                new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                {
                    ["fallback"] = "poll",
                });
        }
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task SeedAsync(
        InMemoryFactory factory, Guid tenantId,
        params (Guid Id, string Type, long Seq)[] rows)
    {
        await using var seed = await factory.CreateDbContextAsync();
        foreach (var r in rows)
        {
            seed.PlatformEvents.Add(new PlatformEvent
            {
                Id = r.Id,
                Type = r.Type,
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow.AddSeconds(r.Seq),
                SequenceNumber = r.Seq,
            });
        }
        await seed.SaveChangesAsync();
    }

    private sealed class InMemoryFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName = $"sse-poll-{Guid.NewGuid():N}";

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
