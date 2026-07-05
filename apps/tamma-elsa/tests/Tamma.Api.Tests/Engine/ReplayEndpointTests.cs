using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Engine.Replay;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Story 4-8 (black-box replay) — endpoint coverage for
/// <see cref="EngineEndpoints.ReplayRun"/> against the fixture's tenant Postgres so
/// the correlationId JSONB lookup + BIGSERIAL sequence exercise the real
/// EF/Postgres path (like <see cref="EventQueryEndpointTests"/>).
///
/// <para>Proves: a run reconstructs its full state; an <c>upTo</c> sequence /
/// timestamp returns the as-of-then state (not the final); tenant isolation (a
/// tenant can only replay THEIR OWN run — another tenant's correlationId is a 404,
/// no IDOR); null-tenant fails closed (404); an unknown run is a 404; bad
/// <c>upTo</c>/<c>from</c> are 400; the diff (<c>from</c>) is present; and replay is
/// READ-ONLY (the tenant event count is unchanged — no writes, no activity
/// execution).</para>
/// </summary>
[TestFixture]
public class ReplayEndpointTests
{
    private IServiceScope _scope = null!;
    private IEventRepository _events = null!;
    private IReplayService _replay = null!;
    private ITenantDbContextFactory _factory = null!;

    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _replay = _scope.ServiceProvider.GetRequiredService<IReplayService>();
        _factory = _scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── full run ────────────────────────────────────────────────────────────

    [Test]
    public async Task Replay_FullRun_ReconstructsState()
    {
        var tenantId = Guid.NewGuid();
        await SeedRunAsync(tenantId, "run-full", new[]
        {
            "WORKFLOW.STEP_STARTED",
            "LLM.CALL.SUCCESS",
            "CODE.GENERATED.SUCCESS",
            "GIT.PR_OPENED.SUCCESS",
            "WORKFLOW.COMPLETED",
        }, issueNumber: 7);

        var doc = await CallOkAsync(tenantId, "run-full");

        doc.GetProperty("eventsReplayed").GetInt32().Should().Be(5);
        doc.GetProperty("totalEvents").GetInt32().Should().Be(5);
        doc.GetProperty("replayedToEnd").GetBoolean().Should().BeTrue();
        doc.GetProperty("status").GetString().Should().Be("completed");
        doc.GetProperty("stepReached").GetString().Should().Be("WORKFLOW.COMPLETED");
        doc.GetProperty("issueNumber").GetInt32().Should().Be(7);
        doc.GetProperty("aiDecisions").GetArrayLength().Should().Be(1);
        doc.GetProperty("codeChanges").GetArrayLength().Should().Be(2);
    }

    // ── point-in-time: upTo sequence ──────────────────────────────────────────

    [Test]
    public async Task Replay_UpToMidSequence_ReturnsAsOfThen_NotFinal()
    {
        var tenantId = Guid.NewGuid();
        await SeedRunAsync(tenantId, "run-seq", new[]
        {
            "WORKFLOW.STEP_STARTED",   // index 0
            "LLM.CALL.SUCCESS",        // index 1
            "CODE.GENERATED.SUCCESS",  // index 2  ← replay up to here
            "GIT.PR_OPENED.SUCCESS",   // index 3
            "WORKFLOW.COMPLETED",      // index 4
        });

        // The real BIGSERIAL sequences, oldest-first.
        var seqs = (await _events.ListByCorrelationIdAsync(tenantId, "run-seq"))
            .Select(e => e.SequenceNumber).ToList();
        var mid = seqs[2];

        var doc = await CallOkAsync(tenantId, "run-seq", upTo: mid.ToString());

        doc.GetProperty("eventsReplayed").GetInt32().Should().Be(3);
        doc.GetProperty("replayedToEnd").GetBoolean().Should().BeFalse();
        doc.GetProperty("stepReached").GetString().Should().Be("CODE.GENERATED.SUCCESS");
        doc.GetProperty("status").GetString().Should().Be("running",
            "the terminal WORKFLOW.COMPLETED is after the replay point");
        doc.GetProperty("atSequenceNumber").GetInt64().Should().Be(mid);
    }

    // ── point-in-time: upTo timestamp ─────────────────────────────────────────

    [Test]
    public async Task Replay_UpToTimestamp_ReturnsAsOfThen()
    {
        var tenantId = Guid.NewGuid();
        // Insertion order == chronological order, so SequenceNumber tracks CreatedAt.
        await SeedRunAsync(tenantId, "run-ts", new[]
        {
            ("A.ONE", Base),
            ("A.TWO", Base.AddHours(1)),
            ("A.THREE", Base.AddHours(2)),
        });

        // Cut at Base+1h — only the first two events qualify (half-inclusive at the cut).
        var cut = new DateTimeOffset(Base.AddHours(1)).ToString("O");
        var doc = await CallOkAsync(tenantId, "run-ts", upTo: cut);

        doc.GetProperty("eventsReplayed").GetInt32().Should().Be(2);
        doc.GetProperty("stepReached").GetString().Should().Be("A.TWO");
    }

    // ── diff (from) ───────────────────────────────────────────────────────────

    [Test]
    public async Task Replay_WithFrom_IncludesDelta()
    {
        var tenantId = Guid.NewGuid();
        await SeedRunAsync(tenantId, "run-diff", new[]
        {
            "WORKFLOW.STEP_STARTED",
            "LLM.CALL.SUCCESS",
            "GIT.PR_OPENED.SUCCESS",
            "WORKFLOW.COMPLETED",
        });
        var seqs = (await _events.ListByCorrelationIdAsync(tenantId, "run-diff"))
            .Select(e => e.SequenceNumber).ToList();

        // from = seq[1] (the LLM call); upTo = end → delta is seq[2], seq[3].
        var doc = await CallOkAsync(tenantId, "run-diff", from: seqs[1].ToString());

        var delta = doc.GetProperty("delta");
        delta.ValueKind.Should().Be(JsonValueKind.Object);
        delta.GetProperty("fromSequenceNumber").GetInt64().Should().Be(seqs[1]);
        delta.GetProperty("addedEventCount").GetInt32().Should().Be(2);
        delta.GetProperty("addedCodeChanges").GetInt32().Should().Be(1);
    }

    // ── tenant isolation (no IDOR) ────────────────────────────────────────────

    [Test]
    public async Task Replay_TenantIsolation_SeesOnlyOwnRun_SameCorrelationId()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        // Both tenants stamp the IDENTICAL correlationId — a leak would surface here.
        await SeedRunAsync(tenantA, "shared", new[] { "A.ONE", "A.TWO" });
        await SeedRunAsync(tenantB, "shared", new[] { "B.ONE", "B.TWO", "B.THREE" });

        var docA = await CallOkAsync(tenantA, "shared");
        docA.GetProperty("eventsReplayed").GetInt32().Should().Be(2,
            "tenant A must replay only tenant A's events, even for a shared correlationId");
        docA.GetProperty("totalEvents").GetInt32().Should().Be(2);

        var docB = await CallOkAsync(tenantB, "shared");
        docB.GetProperty("eventsReplayed").GetInt32().Should().Be(3);
    }

    [Test]
    public async Task Replay_OtherTenantsRun_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        // Only tenant B owns "b-only".
        await SeedRunAsync(tenantB, "b-only", new[] { "B.ONE" });
        await EnsureTenantProvisionedAsync(tenantA); // A exists but owns nothing here

        var (status, _) = await CallAsync(tenantA, "b-only");
        status.Should().Be(StatusCodes.Status404NotFound,
            "a tenant cannot replay another tenant's run (no IDOR)");
    }

    // ── null-tenant fail-closed ───────────────────────────────────────────────

    [Test]
    public async Task Replay_NullTenant_FailsClosed_404()
    {
        var otherTenant = Guid.NewGuid();
        await SeedRunAsync(otherTenant, "private-run", new[] { "X.ONE" });

        var (status, _) = await CallAsync(tenantId: null, correlationId: "private-run");
        status.Should().Be(StatusCodes.Status404NotFound,
            "no resolved tenant must never surface another tenant's run");
    }

    // ── unknown run ───────────────────────────────────────────────────────────

    [Test]
    public async Task Replay_UnknownRun_Returns404()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantProvisionedAsync(tenantId);

        var (status, _) = await CallAsync(tenantId, "does-not-exist");
        status.Should().Be(StatusCodes.Status404NotFound);
    }

    // ── fail-loud 400s ────────────────────────────────────────────────────────

    [Test]
    public async Task Replay_BadUpTo_Returns400()
    {
        var tenantId = Guid.NewGuid();

        var (garbage, _) = await CallAsync(tenantId, "run", upTo: "not-a-seq-or-date");
        garbage.Should().Be(StatusCodes.Status400BadRequest);

        var (zero, _) = await CallAsync(tenantId, "run", upTo: "0");
        zero.Should().Be(StatusCodes.Status400BadRequest);

        var (neg, _) = await CallAsync(tenantId, "run", upTo: "-3");
        neg.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Replay_BadFrom_Returns400()
    {
        var tenantId = Guid.NewGuid();

        var (bad, _) = await CallAsync(tenantId, "run", from: "0");
        bad.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── read-only (no writes / no activity execution) ─────────────────────────

    [Test]
    public async Task Replay_IsReadOnly_DoesNotWriteOrExecute()
    {
        var tenantId = Guid.NewGuid();
        await SeedRunAsync(tenantId, "run-ro", new[]
        {
            "WORKFLOW.STEP_STARTED", "LLM.CALL.SUCCESS", "WORKFLOW.COMPLETED",
        });

        var before = await CountEventsAsync(tenantId);

        // Replay a few times (full + point-in-time + diff) — a pure fold must not
        // append, mutate, or re-execute anything.
        await CallOkAsync(tenantId, "run-ro");
        await CallOkAsync(tenantId, "run-ro", upTo: "1");
        await CallOkAsync(tenantId, "run-ro", from: "1");

        var after = await CountEventsAsync(tenantId);
        after.Should().Be(before, "replay is a read-only fold — it writes no events");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<int> CountEventsAsync(Guid tenantId)
    {
        await using var db = await _factory.CreateAsync(tenantId);
        return await db.DomainEvents.IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId);
    }

    private async Task EnsureTenantProvisionedAsync(Guid tenantId)
    {
        var cp = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        if (!await cp.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            cp.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = $"Test {tenantId:N}",
                Slug = $"t-{tenantId:N}",
                Plan = "free"
            });
            await cp.SaveChangesAsync();
        }
        await ApiTestFixture.ProvisionTenantAsync(tenantId);
    }

    private Task SeedRunAsync(Guid tenantId, string correlationId, string[] types, int? issueNumber = null)
        => SeedRunAsync(tenantId, correlationId,
            types.Select(t => (t, (DateTime?)null)).ToArray(), issueNumber);

    private Task SeedRunAsync(Guid tenantId, string correlationId, (string Type, DateTime At)[] events)
        => SeedRunAsync(tenantId, correlationId,
            events.Select(e => (e.Type, (DateTime?)e.At)).ToArray(), null);

    /// <summary>
    /// Insert a run's events one-by-one (so BIGSERIAL sequence tracks insertion
    /// order) with the correlationId stamped into Tags — the same shape
    /// <see cref="IEventRepository.ListByCorrelationIdAsync"/> filters on.
    /// </summary>
    private async Task SeedRunAsync(
        Guid tenantId, string correlationId,
        (string Type, DateTime? At)[] events, int? issueNumber)
    {
        await EnsureTenantProvisionedAsync(tenantId);

        var tags = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["correlationId"] = correlationId,
        });

        var i = 0;
        foreach (var (type, at) in events)
        {
            await using var db = await _factory.CreateAsync(tenantId);
            db.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                TenantId = tenantId,
                IssueNumber = issueNumber,
                Tags = tags,
                Metadata = "{\"workflowVersion\":\"1.0.0\",\"eventSource\":\"system\"}",
                Data = "{}",
                CreatedAt = at ?? Base.AddSeconds(i),
            });
            await db.SaveChangesAsync();
            i++;
        }
    }

    private async Task<(int Status, JsonElement Body)> CallAsync(
        Guid? tenantId, string correlationId, string? upTo = null, string? from = null)
    {
        var tc = new TenantContext();
        if (tenantId is Guid tid) tc.SetTenantId(tid);

        var result = await EngineEndpoints.ReplayRun(correlationId, _replay, tc, upTo, from);

        var ctx = new DefaultHttpContext { RequestServices = ApiTestFixture.Factory.Services };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = ctx.Response.Body.Length == 0
            ? default
            : JsonDocument.Parse(ctx.Response.Body).RootElement.Clone();
        return (ctx.Response.StatusCode, body);
    }

    private async Task<JsonElement> CallOkAsync(
        Guid? tenantId, string correlationId, string? upTo = null, string? from = null)
    {
        var (status, body) = await CallAsync(tenantId, correlationId, upTo, from);
        status.Should().Be(StatusCodes.Status200OK);
        return body;
    }
}
