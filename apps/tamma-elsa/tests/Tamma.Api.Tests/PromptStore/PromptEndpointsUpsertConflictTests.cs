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
using Tamma.Api.Dtos.Prompts;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Task #13 — verifies that the prompt-store upsert endpoints map a
/// Postgres <c>23505</c> unique-violation (raised when two concurrent
/// same-key upserts race past the repository's check-then-insert window)
/// to HTTP <c>409 Conflict</c> with the canonical
/// <c>CONCURRENT_UPSERT_CONFLICT</c> code, instead of leaking it as a 500.
///
/// <para>The repository is mocked so we can deterministically throw a
/// <see cref="DbUpdateException"/> wrapping a <see cref="PostgresException"/>
/// with <c>SqlState = "23505"</c> — the exact exception shape produced by
/// the real <c>prompt_overrides</c> <c>UNIQUE NULLS NOT DISTINCT</c>
/// constraint.</para>
/// </summary>
[TestFixture]
public class PromptEndpointsUpsertConflictTests
{
    [Test]
    public async Task UpsertPrompt_OnUniqueViolation_Returns409Conflict()
    {
        var repo = new Mock<IPromptRepository>(MockBehavior.Strict);
        repo.Setup(r => r.UpsertAsync(It.IsAny<PromptOverride>(), It.IsAny<Guid?>()))
            .ThrowsAsync(MakeUniqueViolation());

        var store = new PromptStoreService(repo.Object);
        var events = new PromptEventsService(Mock.Of<IEventRepository>());
        var tenantContext = new TenantContext();

        var result = await PromptEndpoints.UpsertPrompt(
            role: "developer",
            action: "implement-feature",
            req: new UpsertPromptRequest(
                Template: "User template",
                SystemPrompt: "Sys",
                Variables: Array.Empty<string>(),
                EnableTools: false,
                MaxTokens: 4096),
            store: store,
            events: events,
            principal: PrincipalWith(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            tenantContext: tenantContext,
            modeProvider: new StubModeProvider(TammaMode.SingleUser));

        await AssertIsConflict(result);
    }

    [Test]
    public async Task UpsertSystemPrompt_OnUniqueViolation_Returns409Conflict()
    {
        var repo = new Mock<IPromptRepository>(MockBehavior.Strict);
        repo.Setup(r => r.UpsertAsync(It.IsAny<PromptOverride>(), It.IsAny<Guid?>()))
            .ThrowsAsync(MakeUniqueViolation());

        var store = new PromptStoreService(repo.Object);
        var events = new PromptEventsService(Mock.Of<IEventRepository>());
        var tenantContext = new TenantContext();

        var result = await PromptEndpoints.UpsertSystemPrompt(
            role: "developer",
            req: new UpsertPromptRequest(
                Template: "Role system",
                SystemPrompt: null,
                Variables: Array.Empty<string>(),
                EnableTools: false,
                MaxTokens: 4096),
            store: store,
            events: events,
            principal: PrincipalWith(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            tenantContext: tenantContext,
            modeProvider: new StubModeProvider(TammaMode.SingleUser));

        await AssertIsConflict(result);
    }

    [Test]
    public void IsUniqueViolation_OnlyMatchesSqlState23505()
    {
        // Defence-in-depth: only the exact 23505 SqlState produces 409.
        var ex42P01 = new DbUpdateException("missing table",
            new PostgresException("undefined_table", "ERROR", "ERROR", "42P01"));
        PromptEndpoints.IsUniqueViolation(ex42P01).Should().BeFalse();

        var ex23505 = MakeUniqueViolation();
        PromptEndpoints.IsUniqueViolation(ex23505).Should().BeTrue();

        var exGeneric = new DbUpdateException("generic", new InvalidOperationException("not pg"));
        PromptEndpoints.IsUniqueViolation(exGeneric).Should().BeFalse();
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

    private static async Task AssertIsConflict(IResult result)
    {
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
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
