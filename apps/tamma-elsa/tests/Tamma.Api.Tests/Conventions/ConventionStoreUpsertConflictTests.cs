using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Conventions;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Task #13 — verifies that the convention-store upsert endpoints map a
/// Postgres <c>23505</c> unique-violation (raised when two concurrent
/// same-key upserts race past the repository's check-then-insert window)
/// to HTTP <c>409 Conflict</c> with the canonical
/// <c>CONCURRENT_UPSERT_CONFLICT</c> code, instead of leaking it as a 500.
///
/// <para>The store is mocked so we can deterministically throw a
/// <see cref="DbUpdateException"/> wrapping a <see cref="PostgresException"/>
/// with <c>SqlState = "23505"</c> — the exact exception shape produced by
/// the real repository when two threads collide on the
/// <c>UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, scope, role, action)</c>
/// constraint.</para>
/// </summary>
[TestFixture]
public class ConventionStoreUpsertConflictTests
{
    private static readonly Guid TenantId = Guid.Parse("ee000000-0000-0000-0000-000000000001");
    private static readonly Guid AdminUserId = Guid.Parse("ee000000-0000-0000-0000-000000000002");

    [Test]
    public async Task UpsertTenantOverride_OnUniqueViolation_Returns409Conflict()
    {
        var store = new Mock<IConventionStore>(MockBehavior.Strict);
        store.Setup(s => s.UpsertAsync(
                It.IsAny<Guid>(),
                It.IsAny<AgentRole>(),
                It.IsAny<AgentAction>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeUniqueViolation());

        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(TenantId);

        var result = await ConventionStoreEndpoints.UpsertTenantOverride(
            role: "developer",
            action: "implement-feature",
            req: new UpsertConventionRequest("Use camelCase.", Enabled: true),
            store: store.Object,
            principal: PrincipalWith(AdminUserId),
            tenantContext: tenantContext,
            modeProvider: new StubModeProvider(TammaMode.SaaS),
            ct: CancellationToken.None);

        await AssertIsConflict(result);
    }

    [Test]
    public async Task UpsertSystemDefault_OnUniqueViolation_Returns409Conflict()
    {
        var store = new Mock<IConventionStore>(MockBehavior.Strict);
        store.Setup(s => s.UpsertSystemDefaultAsync(
                It.IsAny<AgentRole>(),
                It.IsAny<AgentAction>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeUniqueViolation());

        var result = await ConventionStoreEndpoints.UpsertSystemDefault(
            role: "developer",
            action: "implement-feature",
            req: new UpsertConventionRequest("System baseline body.", Enabled: true),
            store: store.Object,
            principal: PrincipalWith(AdminUserId),
            ct: CancellationToken.None);

        await AssertIsConflict(result);
    }

    [Test]
    public void IsUniqueViolation_OnlyMatchesSqlState23505()
    {
        // A different SqlState (e.g. 42P01 missing-table) must NOT trigger
        // the conflict mapping — defence-in-depth against accidentally
        // returning 409 on the wrong error class.
        var ex42P01 = new DbUpdateException("table missing",
            new PostgresException("undefined_table", "ERROR", "ERROR", "42P01"));
        ConventionStoreEndpoints.IsUniqueViolation(ex42P01).Should().BeFalse();

        var ex23505 = MakeUniqueViolation();
        ConventionStoreEndpoints.IsUniqueViolation(ex23505).Should().BeTrue();

        // A DbUpdateException with a NON-Postgres inner is also not a match.
        var exGeneric = new DbUpdateException("generic", new InvalidOperationException("not pg"));
        ConventionStoreEndpoints.IsUniqueViolation(exGeneric).Should().BeFalse();
    }

    // ---------------- helpers ----------------

    private static DbUpdateException MakeUniqueViolation()
        => new(
            "duplicate key value violates unique constraint",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                "23505"));

    private static ClaimsPrincipal PrincipalWith(Guid userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class StubModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    /// <summary>
    /// Execute the IResult against a synthetic <see cref="HttpContext"/>,
    /// then assert the response carries 409 + the canonical code shape.
    /// Mirrors the pattern in <c>PromptEndpointsTenantAdminTests</c>.
    /// </summary>
    private static async Task AssertIsConflict(IResult result)
    {
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        // Wire a tiny RequestServices so IResult.ExecuteAsync (which may
        // resolve ILogger) doesn't NRE.
        ctx.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var body = await reader.ReadToEndAsync();
        body.Should().Contain("CONCURRENT_UPSERT_CONFLICT");
    }
}
