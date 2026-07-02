using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 36-2 — unit tests for
/// <see cref="ComputeTenantDimensionalRollupActivity.ComputeAsync"/>: dimension
/// bucketing (incl. NULL buckets reconciling to the grand total), cost-basis
/// classification, platform-billed margin, idempotent replay, and checkpoint
/// advance. Uses the shared InMemory harness (relational-only guarantees are
/// proven by the Postgres Testcontainer suite in Tamma.Api.Tests).
/// </summary>
[TestFixture]
public class ComputeTenantDimensionalRollupTests
{
    private static readonly DateTime Hour = new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private FakeTenantDbContextFactory _tenantFactory = null!;
    private Mock<IPlatformEventPublisher> _publisher = null!;
    private List<IDisposable> _opened = null!;

    [SetUp]
    public void SetUp()
    {
        _opened = new List<IDisposable>();
        _tenantFactory = new FakeTenantDbContextFactory(_opened);
        _publisher = new Mock<IPlatformEventPublisher>();
        _publisher
            .Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformEvent evt, CancellationToken _) => evt);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var ctx in _opened) ctx.Dispose();
    }

    private static string Tags(params (string k, string? v)[] pairs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return JsonSerializer.Serialize(d);
    }

    private static DomainEvent Llm(long seq, DateTime at, string tags, decimal cost, long tin, long tout) => new()
    {
        Id = Guid.NewGuid(),
        Type = "LLM.CALL.SUCCESS",
        CreatedAt = at,
        SequenceNumber = seq,
        Tags = tags,
        Metadata = "{}",
        Data = JsonSerializer.Serialize(new { costUsd = cost, inputTokens = tin, outputTokens = tout }),
    };

    private async Task RunAsync(Guid tenantId, IAnalyticsPricingConfig pricing, bool reset = false) =>
        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            _tenantFactory, _publisher.Object, tenantId, Hour, pricing, reset, null, CancellationToken.None);

    // ── AC2 / AC3 — bucketing by provider + agent, NULL bucket reconciles ──
    [Test]
    public async Task ComputeAsync_BucketsByProviderAndAgent_NullReconcilesToGrandTotal()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(
            // provider A, agent x
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic"), ("agent_id", "x"), ("billing_mode", "platform")), 0.10m, 100, 50),
            // provider A, agent x (same tuple → sums)
            Llm(2, Hour.AddMinutes(6), Tags(("provider", "anthropic"), ("agent_id", "x"), ("billing_mode", "platform")), 0.20m, 200, 100),
            // provider B, no agent tag → AgentId NULL bucket
            Llm(3, Hour.AddMinutes(7), Tags(("provider", "openai"), ("billing_mode", "platform")), 0.40m, 400, 200));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.ToListAsync();

        rows.Should().HaveCount(2);
        var anthropic = rows.Single(r => r.Provider == "anthropic");
        anthropic.AgentId.Should().Be("x");
        anthropic.TokensIn.Should().Be(300);
        anthropic.CostUsd.Should().Be(0.30m);

        var openai = rows.Single(r => r.Provider == "openai");
        openai.AgentId.Should().BeNull("no agent_id tag buckets under the NULL 'unattributed' bucket");
        openai.TokensIn.Should().Be(400);

        // Reconciliation: Σ per-row == grand total.
        rows.Sum(r => r.TokensIn).Should().Be(700);
        rows.Sum(r => r.CostUsd).Should().Be(0.70m);
    }

    // ── AC4 — cost basis from billing_mode tag ──
    [Test]
    public async Task ComputeAsync_ResolvesCostBasis_FromBillingModeTag()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic"), ("billing_mode", "byok")), 1.00m, 10, 10),
            Llm(2, Hour.AddMinutes(6), Tags(("provider", "anthropic"), ("billing_mode", "platform")), 2.00m, 20, 20));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.ToListAsync();

        rows.Should().HaveCount(2, "byok and platform are distinct cost-basis buckets");
        rows.Single(r => r.CostBasis == CostBasis.Byok).CostUsd.Should().Be(1.00m);
        rows.Single(r => r.CostBasis == CostBasis.Platform).CostUsd.Should().Be(2.00m);
    }

    [Test]
    public async Task ComputeAsync_DefaultsToPlatform_WhenNoBillingMode()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic")), 1.00m, 10, 10));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var row = await verify.AnalyticsUsageHourly.SingleAsync();
        row.CostBasis.Should().Be(CostBasis.Platform, "absent billing_mode → platform default");
    }

    // ── AC5 — platform margin applied; byok → 0 ──
    [Test]
    public async Task ComputeAsync_AppliesMargin_ToPlatformOnly()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic"), ("billing_mode", "platform")), 1.00m, 10, 10),
            Llm(2, Hour.AddMinutes(6), Tags(("provider", "anthropic"), ("billing_mode", "byok")), 1.00m, 10, 10));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0.20m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var platform = await verify.AnalyticsUsageHourly.SingleAsync(r => r.CostBasis == CostBasis.Platform);
        var byok = await verify.AnalyticsUsageHourly.SingleAsync(r => r.CostBasis == CostBasis.Byok);

        platform.PlatformBilledUsd.Should().Be(1.20m, "platform bills cost * (1 + margin)");
        byok.PlatformBilledUsd.Should().Be(0m, "Tamma never marks up a BYOK call");
    }

    [Test]
    public async Task ComputeAsync_ZeroMargin_WhenNullPricingConfig()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic"), ("billing_mode", "platform")), 2.50m, 10, 10));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new NullAnalyticsPricingConfig());

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var row = await verify.AnalyticsUsageHourly.SingleAsync();
        row.PlatformBilledUsd.Should().Be(2.50m, "null pricing config → zero margin → billed == cost");
    }

    // ── AC1 — agent dispatches (REAL underscore family, no provider tag) land in
    //    the NULL-provider bucket; diagnostics contribute their own provider row ──
    [Test]
    public async Task ComputeAsync_CountsAgentDispatches_InNullProviderBucket_AndFoldsDiagnostics()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(
            // Story 38-2 mediation dispatch events — underscore family, NO provider
            // tag (they carry repo/operation/correlationId only). A dotted LIKE
            // pattern never matched these; the fix counts them under NULL provider.
            new DomainEvent { Id = Guid.NewGuid(), Type = "AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS",
                CreatedAt = Hour.AddMinutes(1), SequenceNumber = 10,
                Tags = Tags(("operation", "run_trigger"), ("repo", "acme/app")), Metadata = "{}", Data = "{}" },
            new DomainEvent { Id = Guid.NewGuid(), Type = "AGENT_DISPATCH.RUN_TRIGGERED.FAILED",
                CreatedAt = Hour.AddMinutes(2), SequenceNumber = 11,
                Tags = Tags(("operation", "run_trigger"), ("repo", "acme/app")), Metadata = "{}", Data = "{}" },
            // A follow-up poll must NOT count as a dispatch.
            new DomainEvent { Id = Guid.NewGuid(), Type = "AGENT_DISPATCH.RUN_POLLED.SUCCESS",
                CreatedAt = Hour.AddMinutes(2), SequenceNumber = 12,
                Tags = Tags(("operation", "run_poll"), ("repo", "acme/app")), Metadata = "{}", Data = "{}" });
        db.ProviderDiagnostics.Add(new ProviderDiagnostic
        {
            Id = Guid.NewGuid(), ProviderKey = "anthropic", AgentType = "developer",
            InputTokens = 500, OutputTokens = 250, Cost = 0.90m, CreatedAt = Hour.AddMinutes(3),
        });
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.ToListAsync();

        rows.Sum(r => r.AgentDispatches).Should().Be(2, "only RUN_TRIGGERED counts; RUN_POLLED does not");
        var dispatchRow = rows.Single(r => r.AgentDispatches > 0);
        dispatchRow.Provider.Should().BeNull("dispatch events carry no provider → NULL bucket");
        dispatchRow.CostUsd.Should().Be(0m);

        var diag = rows.Single(r => r.AgentId == "developer");
        diag.Provider.Should().Be("anthropic");
        diag.TokensIn.Should().Be(500);
        diag.TokensOut.Should().Be(250);
        diag.CostUsd.Should().Be(0.90m);
    }

    // ── Fix 2 — diagnostic TokensUsed (back-compat total) is attributed when the
    //    InputTokens/OutputTokens split is unset (the LlmProxyService writer). ──
    [Test]
    public async Task ComputeAsync_AttributesDiagnosticTokensUsed_WhenSplitIsZero()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.ProviderDiagnostics.AddRange(
            // LlmProxyService populates only TokensUsed; In/Out stay 0.
            new ProviderDiagnostic { Id = Guid.NewGuid(), ProviderKey = "anthropic",
                TokensUsed = 1000, InputTokens = 0, OutputTokens = 0, Cost = 0.50m, CreatedAt = Hour.AddMinutes(1) },
            // A writer that DOES split must not be double-counted from TokensUsed.
            new ProviderDiagnostic { Id = Guid.NewGuid(), ProviderKey = "openai",
                TokensUsed = 999, InputTokens = 300, OutputTokens = 200, Cost = 0.25m, CreatedAt = Hour.AddMinutes(2) });
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.ToListAsync();

        var total = rows.Single(r => r.Provider == "anthropic");
        total.TokensIn.Should().Be(1000, "TokensUsed total attributed to TokensIn when split is 0");
        total.TokensOut.Should().Be(0);

        var split = rows.Single(r => r.Provider == "openai");
        split.TokensIn.Should().Be(300, "split columns used verbatim — TokensUsed ignored to avoid double-count");
        split.TokensOut.Should().Be(200);
    }

    // ── Fix 6 — cost is NOT double-counted when a diagnostic and an LLM event
    //    describe the same call (shared correlationId; diagnostic is authoritative). ──
    [Test]
    public async Task ComputeAsync_NoCostDoubleCount_WhenDiagnosticAndEventShareCorrelationId()
    {
        var tenantId = Guid.NewGuid();
        var corr = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(
            Llm(1, Hour.AddMinutes(5),
                Tags(("provider", "anthropic"), ("correlationId", corr.ToString())), 0.10m, 100, 50));
        db.ProviderDiagnostics.Add(new ProviderDiagnostic
        {
            Id = Guid.NewGuid(), ProviderKey = "anthropic", CorrelationId = corr,
            InputTokens = 100, OutputTokens = 50, Cost = 0.90m, CreatedAt = Hour.AddMinutes(5),
        });
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.ToListAsync();

        rows.Sum(r => r.CostUsd).Should().Be(0.90m,
            "the event's 0.10 is suppressed — the diagnostic (0.90) is the authoritative cost for that correlationId");
    }

    // ── AC6 — idempotent replay (whole-bucket overwrite) ──
    [Test]
    public async Task ComputeAsync_IdempotentReplay_IdenticalRowsAndMeasures()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic"), ("billing_mode", "platform")), 0.50m, 100, 50));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0.10m));
        await RunAsync(tenantId, new FixedMarginPricing(0.10m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.ToListAsync();

        rows.Should().ContainSingle("replay upserts on the business key, never duplicates");
        rows[0].CostUsd.Should().Be(0.50m);
        rows[0].PlatformBilledUsd.Should().Be(0.55m);
    }

    // ── AC7 — checkpoint advances to the max SequenceNumber ──
    [Test]
    public async Task ComputeAsync_AdvancesCheckpoint_ToMaxSequenceNumber()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(
            Llm(42, Hour.AddMinutes(5), Tags(("provider", "anthropic")), 0.10m, 10, 10),
            Llm(99, Hour.AddMinutes(6), Tags(("provider", "openai")), 0.10m, 10, 10));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var cp = await verify.AnalyticsProjectionCheckpoints
            .SingleAsync(c => c.Stream == AnalyticsProjectionCheckpoint.DimensionalStream);
        cp.LastSequenceNumber.Should().Be(99);
    }

    // ── AC9 — backfill re-run never double-counts (crash-resume shape) ──
    [Test]
    public async Task ComputeAsync_ReRunAfterCheckpoint_NoDoubleCount()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(
            Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic")), 0.25m, 100, 50));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));
        // Re-run the same bucket (simulating a crash-resume / backfill): the
        // whole-bucket overwrite must re-derive the same totals, not add to them.
        await RunAsync(tenantId, new FixedMarginPricing(0m), reset: true);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var row = await verify.AnalyticsUsageHourly.SingleAsync();
        row.TokensIn.Should().Be(100, "whole-bucket overwrite absorbs the replay with no double-count");
        row.CostUsd.Should().Be(0.25m);
    }

    // ── AC1 (Fix 4) — workflow lifecycle counts become their OWN NULL-provider
    //    row keyed by WorkflowDefinitionId, distinct from any provider usage row. ──
    [Test]
    public async Task ComputeAsync_EmitsWorkflowCounts_AsOwnNullProviderRow()
    {
        var tenantId = Guid.NewGuid();
        var defId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        // A provider-attributed usage row that also references the same defId —
        // proves the counts are NOT folded onto it (would double-attribute).
        db.DomainEvents.Add(
            Llm(1, Hour.AddMinutes(5),
                Tags(("provider", "anthropic"), ("workflowDefinitionId", defId.ToString())), 0.10m, 10, 10));
        db.WorkflowInstances.AddRange(
            new WorkflowInstance { Id = Guid.NewGuid(), DefinitionId = defId, Status = "completed",
                Variables = "{}", CreatedAt = Hour.AddMinutes(2), UpdatedAt = Hour.AddMinutes(2) },
            new WorkflowInstance { Id = Guid.NewGuid(), DefinitionId = defId, Status = "failed",
                Variables = "{}", CreatedAt = Hour.AddMinutes(3), UpdatedAt = Hour.AddMinutes(3) });
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var rows = await verify.AnalyticsUsageHourly.Where(r => r.WorkflowDefinitionId == defId).ToListAsync();
        rows.Should().HaveCount(2, "the provider usage row and the NULL-provider workflow-count row are distinct");

        var wfRow = rows.Single(r => r.Provider == null);
        wfRow.WorkflowsStarted.Should().Be(2);
        wfRow.WorkflowsCompleted.Should().Be(1);
        wfRow.WorkflowsFailed.Should().Be(1);
        wfRow.CostUsd.Should().Be(0m, "the workflow-count row carries no cost");

        var usageRow = rows.Single(r => r.Provider == "anthropic");
        usageRow.WorkflowsStarted.Should().Be(0, "counts are not folded onto the provider usage row");
        usageRow.CostUsd.Should().Be(0.10m);

        // Grand-total reconciliation still holds across the split rows.
        rows.Sum(r => r.WorkflowsStarted).Should().Be(2);
        rows.Sum(r => r.CostUsd).Should().Be(0.10m);
    }

    // ── AC1 — emits the completion event ──
    [Test]
    public async Task ComputeAsync_EmitsTenantDimensionalCompleted()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(Llm(1, Hour.AddMinutes(5), Tags(("provider", "anthropic")), 0.10m, 10, 10));
        await db.SaveChangesAsync();

        await RunAsync(tenantId, new FixedMarginPricing(0m));

        _publisher.Verify(p => p.AppendAndPublishAsync(
            It.Is<PlatformEvent>(e =>
                e.Type == AnalyticsRollupEvents.TenantDimensionalRollupCompleted && e.TenantId == tenantId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── AC4 — ResolveCostBasis pure helper ──
    [Test]
    public void ResolveCostBasis_TagWins_ThenDiagnostic_ThenPlatformDefault()
    {
        ComputeTenantDimensionalRollupActivity.ResolveCostBasis("byok", null).Should().Be(CostBasis.Byok);
        ComputeTenantDimensionalRollupActivity.ResolveCostBasis("BYOK", null).Should().Be(CostBasis.Byok);
        ComputeTenantDimensionalRollupActivity.ResolveCostBasis("platform", null).Should().Be(CostBasis.Platform);
        ComputeTenantDimensionalRollupActivity.ResolveCostBasis(null, "byok").Should().Be(CostBasis.Byok);
        ComputeTenantDimensionalRollupActivity.ResolveCostBasis(null, null).Should().Be(CostBasis.Platform);
        ComputeTenantDimensionalRollupActivity.ResolveCostBasis("", "").Should().Be(CostBasis.Platform);
    }
}
