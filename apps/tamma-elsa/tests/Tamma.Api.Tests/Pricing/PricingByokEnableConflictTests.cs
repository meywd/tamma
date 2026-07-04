using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.Security;
using Tamma.Data;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-3 (Fix 2) — two concurrent FIRST-time BYOK enables race past the service's
/// check-then-insert window and both INSERT; the loser trips the
/// <c>ux_tpb_active_provider</c> partial unique index (Postgres 23505) as a raw
/// <see cref="DbUpdateException"/>. The <c>EnableByok</c> endpoint must map that to
/// <c>409 Conflict</c> (<c>BYOK_ENABLE_CONFLICT</c>), NOT leak it as a 500.
///
/// <para>The control-plane context is subclassed to throw the exact
/// <see cref="DbUpdateException"/>/<see cref="PostgresException"/> shape on
/// <c>SaveChangesAsync</c> — the InMemory provider does not enforce the unique index, so
/// the race is simulated deterministically (same technique as
/// <c>PromptEndpointsUpsertConflictTests</c>).</para>
/// </summary>
[TestFixture]
public class PricingByokEnableConflictTests
{
    private const string Key = "sk-fake-byok-key-value";

    [Test]
    public async Task EnableByok_OnUniqueViolation_Returns409Conflict_Noterror500()
    {
        var tenant = Guid.NewGuid();
        await using var db = new ThrowingControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var service = new TenantProviderBillingService(
            db, new NoopCabinet(), new RecordingGateEventRepository(), new NoopResolver(),
            TimeProvider.System, NullLogger<TenantProviderBillingService>.Instance);

        // Single-user mode skips the SaaS eligibility gate → straight to the service,
        // whose SaveChanges throws the simulated 23505.
        var result = await PricingEndpoints.EnableByok(
            "anthropic", new EnableByokRequest(Key), Principal(), new StubTenant(tenant),
            new StubMode(TammaMode.SingleUser), FakeAuthLookup.Default(), service,
            NullLoggerFactory.Instance, CancellationToken.None);

        await AssertConflict(result);
    }

    [Test]
    public void IsUniqueViolation_OnlyMatchesSqlState23505()
    {
        PricingEndpoints.IsUniqueViolation(MakeUniqueViolation()).Should().BeTrue();
        PricingEndpoints.IsUniqueViolation(
            new DbUpdateException("wrong table", new PostgresException("x", "ERROR", "ERROR", "42P01")))
            .Should().BeFalse();
        PricingEndpoints.IsUniqueViolation(
            new DbUpdateException("generic", new InvalidOperationException("not pg")))
            .Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DbUpdateException MakeUniqueViolation() =>
        new("duplicate key value violates unique constraint",
            new PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", "23505"));

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test"));

    private static async Task AssertConflict(IResult result)
    {
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        (await reader.ReadToEndAsync()).Should().Contain("BYOK_ENABLE_CONFLICT");
    }

    // A CP context that throws the simulated 23505 on SaveChanges (the insert path).
    private sealed class ThrowingControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
        : ControlPlaneDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw MakeUniqueViolation();
    }

    private sealed class StubTenant(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class NoopCabinet : IProviderByokSecretCabinet
    {
        public Task<SecretMetadata> WriteAsync(
            Guid tenantId, string providerCanonical, string apiKey, Guid ownerUserId, CancellationToken ct = default) =>
            Task.FromResult(new SecretMetadata(
                Guid.NewGuid(), $"provider/{providerCanonical}/api-key", SecretScope.Tenant, tenantId,
                SecretPurpose.ApiKey, Array.Empty<ConsumerRef>(), ownerUserId, RotationSchedule.None,
                LastRotatedAt: null, NextRotationDueAt: null, ActiveVersionNumber: 1,
                CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow));

        public Task<bool> RemoveAsync(Guid tenantId, string providerCanonical, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class NoopResolver : IProviderCredentialResolver
    {
        public Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public void Invalidate(Guid? tenantId, string providerName) { }
    }
}
