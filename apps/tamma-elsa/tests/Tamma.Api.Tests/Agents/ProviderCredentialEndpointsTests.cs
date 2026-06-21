using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Data;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-3 (AC7) — BYOK management API: register / rotate / delete / list,
/// driven directly against the static endpoint handlers with cabinet fakes.
/// Pins reveal-once (no raw key in the response), cache invalidation on every
/// mutation, whitespace-key rejection, unknown-provider 404, and
/// no-tenant-context handling. (Member-role 403 is enforced by the
/// <c>AgentManage</c> route policy and pinned by <c>AgentManagePermissionTests</c>.)
/// </summary>
[TestFixture]
public class ProviderCredentialEndpointsTests
{
    private const string Key = "sk-tenant-byok-123456";
    private static readonly Guid Tenant = Guid.NewGuid();

    private FakeReveal _reveal = null!;
    private FakeQuery _query = null!;
    private RecordingResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _reveal = new FakeReveal();
        _query = new FakeQuery();
        _resolver = new RecordingResolver();
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));

    private static HttpContext Http() => new DefaultHttpContext();

    private static ITenantContext TenantCtx(Guid? id) => new StubTenant(id);

    // ── Register ──────────────────────────────────────────────────────────

    [Test]
    public async Task Register_CreatesViaReveal_ReturnsTokenNotKey_InvalidatesCache()
    {
        var result = await ProviderCredentialEndpoints.RegisterCredential(
            "anthropic", new SetProviderCredentialRequest(Key),
            Principal(), TenantCtx(Tenant), _reveal, _resolver, Http());

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Created<SetProviderCredentialResponse>>();
        var body = ((Microsoft.AspNetCore.Http.HttpResults.Created<SetProviderCredentialResponse>)result).Value!;
        body.Provider.Should().Be("anthropic");
        body.RevealToken.Should().NotBeNullOrEmpty();
        // The raw key must NEVER round-trip in the response.
        System.Text.Json.JsonSerializer.Serialize(body).Should().NotContain(Key);

        _reveal.CreatedName.Should().Be("provider/anthropic/api-key");
        _reveal.CreatedTenantId.Should().Be(Tenant);
        _reveal.CreatedScope.Should().Be(SecretScope.Tenant);
        _reveal.CreatedPurpose.Should().Be(SecretPurpose.ApiKey);
        _resolver.Invalidated.Should().ContainSingle().Which.Should().Be((Tenant, "anthropic"));
    }

    [Test]
    public async Task Register_WhitespaceKey_Rejected()
    {
        var result = await ProviderCredentialEndpoints.RegisterCredential(
            "anthropic", new SetProviderCredentialRequest("   "),
            Principal(), TenantCtx(Tenant), _reveal, _resolver, Http());

        result.GetType().Name.Should().Contain("BadRequest");
        _reveal.CreatedName.Should().BeNull();
    }

    [Test]
    public async Task Register_UnknownProvider_404()
    {
        var result = await ProviderCredentialEndpoints.RegisterCredential(
            "not-a-provider", new SetProviderCredentialRequest(Key),
            Principal(), TenantCtx(Tenant), _reveal, _resolver, Http());

        result.GetType().Name.Should().Contain("NotFound");
    }

    [Test]
    public async Task Register_NoTenantContext_BadRequest()
    {
        var result = await ProviderCredentialEndpoints.RegisterCredential(
            "anthropic", new SetProviderCredentialRequest(Key),
            Principal(), TenantCtx(null), _reveal, _resolver, Http());

        result.GetType().Name.Should().Contain("BadRequest");
    }

    // ── Rotate ────────────────────────────────────────────────────────────

    [Test]
    public async Task Rotate_ExistingKey_RotatesAndInvalidates()
    {
        _query.Seed(Tenant, "provider/anthropic/api-key", id: Guid.NewGuid(), version: 1);

        var result = await ProviderCredentialEndpoints.RotateCredential(
            "anthropic", new SetProviderCredentialRequest("sk-rotated-9999"),
            Principal(), TenantCtx(Tenant), _query, _reveal, _resolver, Http());

        result.GetType().Name.Should().Contain("Ok");
        _reveal.RotatedSecretId.Should().NotBeNull();
        _resolver.Invalidated.Should().Contain((Tenant, "anthropic"));
    }

    [Test]
    public async Task Rotate_NoExistingKey_404()
    {
        var result = await ProviderCredentialEndpoints.RotateCredential(
            "anthropic", new SetProviderCredentialRequest("sk-rotated-9999"),
            Principal(), TenantCtx(Tenant), _query, _reveal, _resolver, Http());

        result.GetType().Name.Should().Contain("NotFound");
        _reveal.RotatedSecretId.Should().BeNull();
        _resolver.Invalidated.Should().BeEmpty();
    }

    // ── Delete ────────────────────────────────────────────────────────────

    [Test]
    public async Task Delete_ExistingKey_RetiresAndInvalidates()
    {
        var id = Guid.NewGuid();
        _query.Seed(Tenant, "provider/anthropic/api-key", id, version: 2);

        var result = await ProviderCredentialEndpoints.DeleteCredential(
            "anthropic", Principal(), TenantCtx(Tenant), _query, _resolver, Http());

        result.GetType().Name.Should().Contain("NoContent");
        _query.RetiredSecretId.Should().Be(id);
        _query.RetiredVersion.Should().Be(2);
        _resolver.Invalidated.Should().Contain((Tenant, "anthropic"));
    }

    [Test]
    public async Task Delete_NoKey_404()
    {
        var result = await ProviderCredentialEndpoints.DeleteCredential(
            "anthropic", Principal(), TenantCtx(Tenant), _query, _resolver, Http());

        result.GetType().Name.Should().Contain("NotFound");
        _resolver.Invalidated.Should().BeEmpty();
    }

    // ── List ──────────────────────────────────────────────────────────────

    [Test]
    public async Task List_ReturnsMetadataOnly_NoKey()
    {
        _query.Seed(Tenant, "provider/anthropic/api-key", Guid.NewGuid(), version: 1);
        _query.Seed(Tenant, "provider/openai/api-key", Guid.NewGuid(), version: 3);
        // A non-provider secret must be filtered out.
        _query.Seed(Tenant, "db/app-role", Guid.NewGuid(), version: 1);

        var result = await ProviderCredentialEndpoints.ListProviders(
            TenantCtx(Tenant), _query, Http());

        // Ok<anonymous> — extract Value via reflection and serialize it.
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        json.Should().Contain("anthropic");
        json.Should().Contain("openai");
        json.Should().NotContain("db/app-role");
        json.Should().NotContain(Key);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fakes
    // ─────────────────────────────────────────────────────────────────────

    private sealed class StubTenant(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class RecordingResolver : IProviderCredentialResolver
    {
        public List<(Guid?, string)> Invalidated { get; } = new();
        public Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct = default) =>
            Task.FromResult(new ProviderCredential("x", CredentialSource.Platform, null, null));
        public void Invalidate(Guid? tenantId, string providerName) => Invalidated.Add((tenantId, providerName));
    }

    private sealed class FakeReveal : ISecretRevealService
    {
        public string? CreatedName { get; private set; }
        public Guid? CreatedTenantId { get; private set; }
        public SecretScope? CreatedScope { get; private set; }
        public SecretPurpose? CreatedPurpose { get; private set; }
        public Guid? RotatedSecretId { get; private set; }

        public Task<RevealTokenIssueResult> IssueCreateAsync(
            string name, SecretScope scope, Guid? tenantId, SecretPurpose purpose,
            string initialPlaintext, IReadOnlyList<ConsumerRef>? consumerRefs,
            Guid ownerUserId, RotationSchedule? rotationSchedule, CancellationToken ct = default)
        {
            CreatedName = name;
            CreatedTenantId = tenantId;
            CreatedScope = scope;
            CreatedPurpose = purpose;
            return Task.FromResult(new RevealTokenIssueResult(
                Meta(Guid.NewGuid(), name, tenantId, version: 1),
                "REVEAL-TOKEN", DateTimeOffset.UtcNow.AddSeconds(60)));
        }

        public Task<RevealTokenIssueResult> IssueRotateAsync(
            Guid secretId, string newPlaintext, Guid actorUserId, CancellationToken ct = default)
        {
            RotatedSecretId = secretId;
            return Task.FromResult(new RevealTokenIssueResult(
                Meta(secretId, "provider/anthropic/api-key", null, version: 2),
                "REVEAL-TOKEN-2", DateTimeOffset.UtcNow.AddSeconds(60)));
        }

        public Task<RevealTokenConsumeResult> ConsumeAsync(
            string rawToken, RevealCallerContext caller, CancellationToken ct = default) =>
            Task.FromResult(new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.NotFound, null, null, null, null, null));

        public Task<int> SweepExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeQuery : ISecretQueryService
    {
        private readonly List<SecretMetadata> _rows = new();
        public Guid? RetiredSecretId { get; private set; }
        public int? RetiredVersion { get; private set; }

        public void Seed(Guid tenantId, string name, Guid id, int version) =>
            _rows.Add(Meta(id, name, tenantId, version));

        public Task<IReadOnlyList<SecretMetadata>> ListAsync(
            SecretScope scope, Guid? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SecretMetadata>>(
                _rows.Where(r => r.Scope == scope && r.TenantId == tenantId).ToList());

        public Task<SecretMetadata?> GetAsync(
            Guid secretId, SecretScope scope, Guid? tenantId, CancellationToken ct = default) =>
            Task.FromResult(_rows.FirstOrDefault(r =>
                r.Id == secretId && r.Scope == scope && r.TenantId == tenantId));

        public Task<IReadOnlyList<SecretVersion>> ListVersionsAsync(
            Guid secretId, SecretScope scope, Guid? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SecretVersion>>(new List<SecretVersion>());

        public Task<SecretVersionStatus> RetireVersionAsync(
            Guid secretId, int versionNumber, SecretScope scope, Guid? tenantId,
            Guid actorUserId, CancellationToken ct = default)
        {
            RetiredSecretId = secretId;
            RetiredVersion = versionNumber;
            return Task.FromResult(SecretVersionStatus.RetiredGrace);
        }
    }

    private static SecretMetadata Meta(Guid id, string name, Guid? tenantId, int version) =>
        new(id, name, SecretScope.Tenant, tenantId, SecretPurpose.ApiKey,
            Array.Empty<ConsumerRef>(), Guid.Empty, RotationSchedule.None,
            LastRotatedAt: null, NextRotationDueAt: null,
            ActiveVersionNumber: version, CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
}
