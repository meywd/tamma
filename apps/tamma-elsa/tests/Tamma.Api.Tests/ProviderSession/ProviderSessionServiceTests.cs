using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.ProviderSession;

/// <summary>
/// Unit tests for <see cref="ProviderSessionService"/>. These tests exercise
/// the service in isolation (no HTTP pipeline, no DI) using in-memory doubles
/// for the diagnostics collaborator and a stubbed <see cref="IProviderClient"/>.
/// </summary>
[TestFixture]
public class ProviderSessionServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public async Task CreateAsync_ReturnsSessionWithNewHandleAndMetadata()
    {
        var (sut, _, _, _) = BuildSut();

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        session.Handle.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(session.Handle, out _).Should().BeTrue();
        session.Provider.Should().Be("anthropic");
        session.Model.Should().Be("claude-sonnet-4");
        session.TenantId.Should().Be(TenantA);
        session.CreatedAt.Should().Be(session.LastUsed);
    }

    [Test]
    public async Task GetAsync_UpdatesLastUsedTimestamp()
    {
        var (sut, clock, _, _) = BuildSut();

        var created = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        clock.Advance(TimeSpan.FromSeconds(10));

        var fetched = await sut.GetAsync(created.Handle);

        fetched.Should().NotBeNull();
        fetched!.LastUsed.Should().Be(clock.UtcNow.UtcDateTime);
        fetched.LastUsed.Should().BeAfter(fetched.CreatedAt);
    }

    [Test]
    public async Task GetAsync_UnknownHandle_ReturnsNull()
    {
        var (sut, _, _, _) = BuildSut();

        var fetched = await sut.GetAsync(Guid.NewGuid().ToString());

        fetched.Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_RemovesSessionAndReturnsTrue()
    {
        var (sut, _, _, _) = BuildSut();

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        var deleted = await sut.DeleteAsync(session.Handle);
        deleted.Should().BeTrue();

        var fetched = await sut.GetAsync(session.Handle);
        fetched.Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_UnknownHandle_ReturnsFalse()
    {
        var (sut, _, _, _) = BuildSut();
        var deleted = await sut.DeleteAsync(Guid.NewGuid().ToString());
        deleted.Should().BeFalse();
    }

    [Test]
    public async Task ListAsync_FiltersByTenant()
    {
        var (sut, _, _, _) = BuildSut();

        await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        await sut.CreateAsync("openai", "gpt-4o", TenantA);
        await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantB);

        var aSessions = await sut.ListAsync(TenantA);
        var bSessions = await sut.ListAsync(TenantB);

        aSessions.Should().HaveCount(2);
        aSessions.All(s => s.TenantId == TenantA).Should().BeTrue();

        bSessions.Should().HaveCount(1);
        bSessions.Single().TenantId.Should().Be(TenantB);
    }

    [Test]
    public async Task ListAsync_WithNullTenant_ReturnsAllSessions()
    {
        var (sut, _, _, _) = BuildSut();

        await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        await sut.CreateAsync("openai", "gpt-4o", TenantB);

        var all = await sut.ListAsync(null);

        all.Should().HaveCount(2);
    }

    [Test]
    public async Task ExecuteAsync_CallsProviderClient_AndRecordsDiagnostic()
    {
        var (sut, _, diagnostics, client) = BuildSut();

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        client.NextResult = new ProviderInvocationResult(
            Content: "hello world",
            TokensUsed: 42,
            CostUsd: 0.003m,
            DurationMs: 137);

        var result = await sut.ExecuteAsync(session.Handle, new ExecuteRequest(
            Handle: session.Handle,
            Input: "Say hi",
            MaxTokens: 256,
            Temperature: 0.2));

        result.Content.Should().Be("hello world");
        result.TokenUsage.Should().Be(42);
        result.CostUsd.Should().Be(0.003m);
        result.DurationMs.Should().BeGreaterThanOrEqualTo(0);

        diagnostics.Recorded.Should().HaveCount(1);
        var diag = diagnostics.Recorded.Single();
        diag.ProviderKey.Should().Be("anthropic");
        diag.Model.Should().Be("claude-sonnet-4");
        diag.TokensUsed.Should().Be(42);
        diag.Cost.Should().Be(0.003m);
        diag.TenantId.Should().Be(TenantA);
        diag.Success.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_UnknownHandle_Throws()
    {
        var (sut, _, _, _) = BuildSut();

        var act = async () => await sut.ExecuteAsync("ghost", new ExecuteRequest("ghost", "hi", null, null));
        await act.Should().ThrowAsync<ProviderSessionNotFoundException>();
    }

    [Test]
    public async Task ExecuteAsync_ProviderError_RecordsFailureDiagnostic()
    {
        var (sut, _, diagnostics, client) = BuildSut();
        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        client.Throw = new HttpRequestException("boom");

        var act = async () => await sut.ExecuteAsync(session.Handle, new ExecuteRequest(
            Handle: session.Handle,
            Input: "hi",
            MaxTokens: null,
            Temperature: null));

        await act.Should().ThrowAsync<HttpRequestException>();
        diagnostics.Recorded.Should().HaveCount(1);
        diagnostics.Recorded.Single().Success.Should().BeFalse();
        diagnostics.Recorded.Single().ErrorMessage.Should().Contain("boom");
    }

    [Test]
    public async Task ExecuteAsync_UpdatesLastUsedOnCall()
    {
        var (sut, clock, _, client) = BuildSut();
        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        var created = session.CreatedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        client.NextResult = new ProviderInvocationResult("ok", 1, 0m, 1);
        await sut.ExecuteAsync(session.Handle, new ExecuteRequest(session.Handle, "hi", null, null));

        var fetched = await sut.GetAsync(session.Handle);
        fetched!.LastUsed.Should().BeAfter(created);
    }

    /// <summary>
    /// Tenant isolation: a session created by tenant A must not be executable
    /// if the caller's tenant context is tenant B.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_DifferentTenant_ThrowsNotFound()
    {
        var (sut, _, _, client) = BuildSut();

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        client.NextResult = new ProviderInvocationResult("ok", 1, 0m, 1);

        var act = async () => await sut.ExecuteTenantScopedAsync(
            callerTenantId: TenantB,
            handle: session.Handle,
            req: new ExecuteRequest(session.Handle, "hi", null, null));

        await act.Should().ThrowAsync<ProviderSessionNotFoundException>();
    }

    [Test]
    public async Task GetTenantScopedAsync_DifferentTenant_ReturnsNull()
    {
        var (sut, _, _, _) = BuildSut();

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        var foreign = await sut.GetTenantScopedAsync(TenantB, session.Handle);
        var owner = await sut.GetTenantScopedAsync(TenantA, session.Handle);

        foreign.Should().BeNull();
        owner.Should().NotBeNull();
    }

    [Test]
    public async Task DeleteTenantScopedAsync_DifferentTenant_ReturnsFalse()
    {
        var (sut, _, _, _) = BuildSut();

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        var foreignDelete = await sut.DeleteTenantScopedAsync(TenantB, session.Handle);
        foreignDelete.Should().BeFalse();

        // Session still there
        (await sut.GetAsync(session.Handle)).Should().NotBeNull();

        var ownerDelete = await sut.DeleteTenantScopedAsync(TenantA, session.Handle);
        ownerDelete.Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (ProviderSessionService Sut, TestSystemClock Clock,
        RecordingDiagnosticsService Diagnostics, StubProviderClient Client) BuildSut()
    {
        var clock = new TestSystemClock(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var diagnostics = new RecordingDiagnosticsService();
        var client = new StubProviderClient();
        var sut = new ProviderSessionService(client, diagnostics, clock, NullLogger());
        return (sut, clock, diagnostics, client);
    }

    private static Microsoft.Extensions.Logging.ILogger<ProviderSessionService> NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderSessionService>.Instance;
}

/// <summary>
/// Diagnostics test double: captures every call to
/// <see cref="IDiagnosticsService.RecordEventAsync"/> without touching a DB.
/// </summary>
internal sealed class RecordingDiagnosticsService : IDiagnosticsService
{
    public List<ProviderDiagnostic> Recorded { get; } = new();

    public Task<Guid> RecordEventAsync(ProviderDiagnostic diag, CancellationToken ct = default)
    {
        Recorded.Add(diag);
        var id = Guid.NewGuid();
        diag.Id = id;
        return Task.FromResult(id);
    }

    public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(DiagnosticsFilter filter, CancellationToken ct = default)
        => Task.FromResult((Recorded.ToList(), Recorded.Count));

    public Task<DiagnosticsReport> GetReportAsync(Guid? tenantId, DateTime from, DateTime to, BucketSize bucketSize, CancellationToken ct = default)
        => Task.FromResult(new DiagnosticsReport(from, to, bucketSize, Array.Empty<DiagnosticsBucket>(), 0, 0m, 0.0));

    public Task<BudgetStatus> GetBudgetAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult(new BudgetStatus(accountId, DateTime.UtcNow, DateTime.UtcNow, 0m, 0m, 0m, 0, 0, false, false));

    public IReadOnlyList<ProviderDiagnostic> GetRecentEvents(Guid? tenantId, int limit = 50)
        => Recorded.AsReadOnly();
}

internal sealed class StubProviderClient : IProviderClient
{
    public ProviderInvocationResult? NextResult { get; set; }
    public Exception? Throw { get; set; }
    public List<(string Provider, string Model, ExecuteRequest Req)> Calls { get; } = new();

    public Task<ProviderInvocationResult> InvokeAsync(
        string provider, string model, ExecuteRequest req, CancellationToken ct = default)
    {
        Calls.Add((provider, model, req));
        if (Throw is not null) throw Throw;
        return Task.FromResult(NextResult ?? new ProviderInvocationResult("stub", 0, 0m, 0));
    }
}
