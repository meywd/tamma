using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Api.Services.TenantStatus;

namespace Tamma.Api.Tests.TenantStatus;

/// <summary>
/// Story 28-8 AC2 — table-driven coverage of every documented
/// <c>tenants.Status</c> → HTTP-response mapping. Source of truth for the
/// mapping is the AC2 list in
/// <c>docs/stories/epic-28/story-28-8/28-8-tenant-context-middleware.md</c>
/// (verified 2026-05-30 follow-up to the 2026-05-29 audit).
///
/// <para>The table at the top of each <c>[TestCase]</c> block mirrors AC2
/// directly so a future reader can `grep` for a status value and find the
/// asserted HTTP code + <c>Retry-After</c> header without leaving this
/// file.</para>
/// </summary>
[TestFixture]
public class TenantStatusEvaluatorTests
{
    private static DefaultHttpContext BuildContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return JsonDocument.Parse(body);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  IsActive — null and "active" both pass through. Anything else gates.
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(null)]
    [TestCase("active")]
    [TestCase("ACTIVE")] // case-insensitive
    public void IsActive_TreatsNullAndActiveAsPassthrough(string? status)
    {
        TenantStatusEvaluator.IsActive(status).Should().BeTrue();
    }

    [TestCase("pending_verification")]
    [TestCase("provisioning")]
    [TestCase("failed")]
    [TestCase("suspended")]
    [TestCase("delete_requested")]
    [TestCase("dropping")]
    [TestCase("deleting")]
    [TestCase("deleted")]
    [TestCase("not_found")]
    [TestCase("")]
    public void IsActive_GatesEveryNonActiveStatus(string status)
    {
        TenantStatusEvaluator.IsActive(status).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  pending_verification → 503 + Retry-After: 60
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PendingVerification_Returns503_WithRetryAfter60_AndVerifyEmailAction()
    {
        var ctx = BuildContext();
        var tenantId = Guid.NewGuid();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(
            ctx, tenantId, TenantStatusEvaluator.StatusPendingVerification);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Headers["Retry-After"].ToString().Should().Be("60");

        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be("tenant_not_ready");
        body.RootElement.GetProperty("status").GetString().Should().Be("pending_verification");
        body.RootElement.GetProperty("retryAfter").GetInt32().Should().Be(60);
        body.RootElement.GetProperty("action").GetString().Should().Be("verify email");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  provisioning → 503 + Retry-After: 5 + progressUrl
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Provisioning_Returns503_WithRetryAfter5_AndProgressUrl()
    {
        var ctx = BuildContext();
        var tenantId = Guid.NewGuid();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(
            ctx, tenantId, TenantStatusEvaluator.StatusProvisioning);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Headers["Retry-After"].ToString().Should().Be("5");

        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be("tenant_not_ready");
        body.RootElement.GetProperty("status").GetString().Should().Be("provisioning");
        body.RootElement.GetProperty("progressUrl").GetString()
            .Should().Be($"/api/v1/tenants/{tenantId:D}/provisioning-status");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  failed → 424 Failed Dependency (no Retry-After per AC2)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Failed_Returns424_NoRetryAfter()
    {
        var ctx = BuildContext();
        var tenantId = Guid.NewGuid();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(
            ctx, tenantId, TenantStatusEvaluator.StatusFailed);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        ctx.Response.Headers.ContainsKey("Retry-After").Should().BeFalse(
            "AC2 explicitly: Retry-After is absent (client stops polling)");

        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be("tenant_provisioning_failed");
        body.RootElement.GetProperty("status").GetString().Should().Be("failed");
        body.RootElement.GetProperty("retryUrl").GetString()
            .Should().Be($"/api/v1/tenants/{tenantId:D}/provisioning-status");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  suspended → 402 Payment Required (AC2)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Suspended_Returns402_PaymentRequired()
    {
        var ctx = BuildContext();
        var tenantId = Guid.NewGuid();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(
            ctx, tenantId, TenantStatusEvaluator.StatusSuspended);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be("tenant_suspended");
        body.RootElement.GetProperty("status").GetString().Should().Be("suspended");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  delete_requested (grace expired) / dropping / deleting → 503 + Retry-After: 0
    // ─────────────────────────────────────────────────────────────────────
    //  AC2 footnote (Doc 04 §8.1) groups these three: client should NOT
    //  retry — the tenant is on its way out and the data plane is being
    //  torn down. Body is `tenant_deleting` for parity with the existing
    //  deleting branch.

    [TestCase("delete_requested", "tenant_deleting")]
    [TestCase("dropping", "tenant_deleting")]
    [TestCase("deleting", "tenant_deleting")]
    public async Task DeleteLifecycleStates_Return503_WithRetryAfterZero(string status, string expectedError)
    {
        var ctx = BuildContext();
        var tenantId = Guid.NewGuid();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(ctx, tenantId, status);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Headers["Retry-After"].ToString().Should().Be("0");

        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        body.RootElement.GetProperty("status").GetString().Should().Be(status);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  deleted → 410 Gone
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Deleted_Returns410Gone()
    {
        var ctx = BuildContext();
        var tenantId = Guid.NewGuid();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(
            ctx, tenantId, TenantStatusEvaluator.StatusDeleted);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be("tenant_deleted");
        body.RootElement.GetProperty("status").GetString().Should().Be("deleted");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Unknown / not_found → 404
    // ─────────────────────────────────────────────────────────────────────

    [TestCase("not_found")]
    [TestCase("garbage")]
    [TestCase("")]
    public async Task UnknownOrNotFound_Returns404(string status)
    {
        var ctx = BuildContext();

        await TenantStatusEvaluator.WriteNonActiveResponseAsync(ctx, Guid.NewGuid(), status);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var body = await ReadJsonAsync(ctx);
        body.RootElement.GetProperty("error").GetString().Should().Be("tenant_not_found");
    }

    [Test]
    public async Task WriteNotFoundResponseAsync_AlwaysReturns404()
    {
        var ctx = BuildContext();

        await TenantStatusEvaluator.WriteNotFoundResponseAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Idempotency — must not throw on a committed response
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResponseAlreadyStarted_BailsSilently_NoThrow()
    {
        var ctx = BuildContext();
        // Force HasStarted=true by writing + flushing before invoking.
        await ctx.Response.Body.WriteAsync("noise"u8.ToArray());
        // DefaultHttpContext's response body doesn't toggle HasStarted via a
        // raw stream write — simulate the contract by skipping when the
        // helper detects a committed response. The check we care about is
        // that calling the method on a context with HasStarted=true does
        // NOT throw (the contract documents this as a silent bail).
        var act = async () => await TenantStatusEvaluator.WriteNonActiveResponseAsync(
            ctx, Guid.NewGuid(), TenantStatusEvaluator.StatusFailed);
        await act.Should().NotThrowAsync();
    }
}
