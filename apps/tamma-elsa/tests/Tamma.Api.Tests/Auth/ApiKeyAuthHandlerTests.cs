using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-7 — branch coverage for <see cref="ApiKeyAuthHandler"/>:
/// prefix routing, tenant resolver wiring, legacy fallback, and the
/// scope guards inherited from the pre-Epic-28 handler.
/// </summary>
[TestFixture]
public class ApiKeyAuthHandlerTests
{
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<IUserRepository> _userRepo = null!;
    private Mock<IInstallationRepository> _instRepo = null!;
    private Mock<ITenantRepository> _tenantRepo = null!;
    private Mock<ITenantConnectionResolver> _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _apiKeyRepo = new Mock<IApiKeyRepository>(MockBehavior.Strict);
        _apiKeyRepo.Setup(r => r.UpdateLastUsedAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);

        _userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        _instRepo = new Mock<IInstallationRepository>(MockBehavior.Loose);
        _tenantRepo = new Mock<ITenantRepository>(MockBehavior.Loose);
        _resolver = new Mock<ITenantConnectionResolver>(MockBehavior.Loose);
    }

    // ── Test infrastructure ──────────────────────────────────────────

    private async Task<(AuthenticateResult Result, HttpContext Context)> RunAsync(
        string? authHeader,
        bool allowLegacy = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_apiKeyRepo.Object);
        services.AddSingleton(_userRepo.Object);
        services.AddSingleton(_instRepo.Object);
        services.AddSingleton(_tenantRepo.Object);
        services.AddSingleton(_resolver.Object);

        var sp = services.BuildServiceProvider();

        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeyAuthHandler.LegacyFallbackConfigKey] = allowLegacy.ToString(),
            });
        var config = configBuilder.Build();

        var optionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(
            new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            sp,
            config);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/v1/something";
        if (authHeader is not null)
            ctx.Request.Headers["Authorization"] = authHeader;

        var scheme = new AuthenticationScheme(
            "ApiKey", "ApiKey", typeof(ApiKeyAuthHandler));
        await handler.InitializeAsync(scheme, ctx);

        var result = await handler.AuthenticateAsync();
        return (result, ctx);
    }

    private static ApiKey BuildKey(
        Guid? id = null,
        string scope = "user",
        Guid? tenantId = null,
        Guid? ownerId = null,
        DateTime? revokedAt = null)
    {
        return new ApiKey
        {
            Id = id ?? Guid.NewGuid(),
            Scope = scope,
            OwnerId = (ownerId ?? Guid.NewGuid()).ToString(),
            KeyHash = "stub",
            KeyPrefix = "tamma_sk_xx",
            Label = "test",
            Permissions = Array.Empty<string>(),
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
        };
    }

    private void SeedKeyLookup(string rawKey, ApiKey? returned)
    {
        var sha = ApiKeyHasher.Hash(rawKey);
        _apiKeyRepo.Setup(r => r.GetByHashAsync(sha))
            .ReturnsAsync(returned);
    }

    private void SeedLegacyKeyLookup(string rawKey, ApiKey? returnedFromSha, ApiKey? returnedFromScrypt)
    {
        var sha = ApiKeyHasher.Hash(rawKey);
        var scrypt = ApiKeyHasher.LegacyScryptHash(rawKey);
        _apiKeyRepo.Setup(r => r.GetByHashAsync(sha)).ReturnsAsync(returnedFromSha);
        _apiKeyRepo.Setup(r => r.GetByHashAsync(scrypt)).ReturnsAsync(returnedFromScrypt);
    }

    // ── No-result paths ──────────────────────────────────────────────

    [Test]
    public async Task NoAuthHeader_ReturnsNoResult()
    {
        var (result, _) = await RunAsync(authHeader: null);
        result.None.Should().BeTrue();
    }

    [Test]
    public async Task NonApiKeyToken_ReturnsNoResult_NotFailure()
    {
        // Anything that doesn't start with "tamma_sk_" is some other scheme
        // (typically a JWT). The handler must yield NoResult so the JWT
        // bearer handler can pick the request up.
        var (result, _) = await RunAsync("Bearer eyJhbGciOiJIUzI1NiJ9.something.thing");
        result.None.Should().BeTrue();
    }

    // ── Tenant-scoped keys ───────────────────────────────────────────

    [Test]
    public async Task TenantPrefixedKey_HappyPath_PopulatesTenantIdItem()
    {
        var tid = Guid.NewGuid();
        var ownerUser = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(scope: "user", tenantId: tid, ownerId: ownerUser);

        SeedKeyLookup(rawKey, apiKey);
        _userRepo.Setup(r => r.GetByIdAsync(ownerUser))
            .ReturnsAsync(new User
            {
                Id = ownerUser, Email = "u@e", Role = "admin",
                AuthMethod = "email", IsActive = true
            });

        var (result, ctx) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeTrue();
        ctx.Items[ApiKeyAuthHandler.TenantIdItemKey].Should().Be(tid);
        _resolver.Verify(
            r => r.GetDataSourceAsync(tid, It.IsAny<CancellationToken>()),
            Times.Once,
            "tenant-prefixed keys must warm the per-tenant data source");
    }

    [Test]
    public async Task TenantPrefixedKey_HashMissInCp_Returns401()
    {
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(Guid.NewGuid());
        SeedKeyLookup(rawKey, null);

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
        _resolver.Verify(
            r => r.GetDataSourceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the resolver must not be called when the hash lookup misses (defence in depth)");
    }

    [Test]
    public async Task TenantPrefixedKey_PrefixTenantMismatchesStoredTenant_Returns401()
    {
        // Defence-in-depth: prefix says tenant A, stored row says tenant B.
        // Could only happen if a key was tampered with — fail closed.
        var prefixTid = Guid.NewGuid();
        var storedTid = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(prefixTid);
        var apiKey = BuildKey(scope: "user", tenantId: storedTid);

        SeedKeyLookup(rawKey, apiKey);

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    [Test]
    public async Task TenantPrefixedKey_TenantNotFound_Returns401()
    {
        var tid = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(scope: "user", tenantId: tid);
        SeedKeyLookup(rawKey, apiKey);

        _resolver.Setup(r => r.GetDataSourceAsync(tid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TenantNotFoundException(tid));

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key",
            "tenant-not-found surfaces as the same opaque 401 as a hash mismatch");
    }

    [Test]
    public async Task TenantPrefixedKey_TenantSuspended_Returns401()
    {
        var tid = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(scope: "user", tenantId: tid);
        SeedKeyLookup(rawKey, apiKey);

        _resolver.Setup(r => r.GetDataSourceAsync(tid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TenantNotProvisionedException(tid, "suspended"));

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    [Test]
    public async Task TenantPrefixedKey_RevokedKey_Returns401()
    {
        var tid = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(
            scope: "user",
            tenantId: tid,
            revokedAt: DateTime.UtcNow.AddMinutes(-1));
        SeedKeyLookup(rawKey, apiKey);

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("API key has been revoked");
    }

    [Test]
    public async Task TenantPrefixedKey_RotatingKeyInGrace_StillAuthenticates()
    {
        var tid = Guid.NewGuid();
        var ownerUser = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(
            scope: "user",
            tenantId: tid,
            ownerId: ownerUser,
            revokedAt: DateTime.UtcNow.AddHours(12)); // future = grace
        SeedKeyLookup(rawKey, apiKey);
        _userRepo.Setup(r => r.GetByIdAsync(ownerUser))
            .ReturnsAsync((User?)null); // role defaults to "member"

        var (result, _) = await RunAsync($"Bearer {rawKey}");
        result.Succeeded.Should().BeTrue("future RevokedAt is the rotation grace window");
    }

    // ── Platform & user prefixes ─────────────────────────────────────

    [Test]
    public async Task PlatformPrefixedKey_HappyPath_DoesNotCallResolver()
    {
        var rawKey = ApiKeyPrefixGenerator.GeneratePlatformKey();
        var ownerUser = Guid.NewGuid();
        var apiKey = BuildKey(scope: "user", tenantId: null, ownerId: ownerUser);
        SeedKeyLookup(rawKey, apiKey);
        _userRepo.Setup(r => r.GetByIdAsync(ownerUser))
            .ReturnsAsync(new User
            {
                Id = ownerUser, Email = "u@e", Role = "platform_admin",
                AuthMethod = "email", IsActive = true
            });

        var (result, ctx) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeTrue();
        ctx.Items.ContainsKey(ApiKeyAuthHandler.TenantIdItemKey).Should().BeFalse(
            "platform-admin keys are tenant-agnostic");
        _resolver.Verify(
            r => r.GetDataSourceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task UserPrefixedKey_HappyPath_DoesNotCallResolver()
    {
        var rawKey = ApiKeyPrefixGenerator.GenerateUserKey();
        var ownerUser = Guid.NewGuid();
        var apiKey = BuildKey(scope: "user", tenantId: null, ownerId: ownerUser);
        SeedKeyLookup(rawKey, apiKey);
        _userRepo.Setup(r => r.GetByIdAsync(ownerUser))
            .ReturnsAsync(new User
            {
                Id = ownerUser, Email = "u@e", Role = "member",
                AuthMethod = "email", IsActive = true
            });

        var (result, _) = await RunAsync($"Bearer {rawKey}");
        result.Succeeded.Should().BeTrue();
        _resolver.Verify(
            r => r.GetDataSourceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task UnknownScopeMarker_NeverFallsThroughToLegacy()
    {
        // Construct a malformed tenant prefix (banner + marker correct,
        // tenant segment garbage) and ensure the parser-flagged "Unknown"
        // result short-circuits to 401 without trying the legacy hash path.
        var rawKey = "tamma_sk_t_NOT-VALID-BASE32_random";
        // No SeedKeyLookup — strict mock would throw if called.

        var (result, _) = await RunAsync($"Bearer {rawKey}");
        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    // ── Legacy fallback ──────────────────────────────────────────────

    [Test]
    public async Task LegacyKey_AllowedByDefault_AuthenticatesViaShaLookup()
    {
        var rawKey = "tamma_sk_legacyrandombody";
        var ownerUser = Guid.NewGuid();
        var apiKey = BuildKey(scope: "user", ownerId: ownerUser);
        SeedLegacyKeyLookup(rawKey, returnedFromSha: apiKey, returnedFromScrypt: null);
        _userRepo.Setup(r => r.GetByIdAsync(ownerUser))
            .ReturnsAsync((User?)null);

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task LegacyKey_FallbackUsesScryptHashWhenShaMisses()
    {
        var rawKey = "tamma_sk_legacyscryptkey";
        var ownerUser = Guid.NewGuid();
        var apiKey = BuildKey(scope: "user", ownerId: ownerUser);
        SeedLegacyKeyLookup(rawKey, returnedFromSha: null, returnedFromScrypt: apiKey);
        _userRepo.Setup(r => r.GetByIdAsync(ownerUser))
            .ReturnsAsync((User?)null);

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task LegacyKey_DisabledByCutoverFlag_Returns401()
    {
        var rawKey = "tamma_sk_legacyrandombody";
        // The repo MUST NOT be queried when the cutover flag is off — the
        // strict mock has no setup for GetByHashAsync, so a call would fail.

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: false);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    [Test]
    public async Task LegacyKey_NoMatch_Returns401()
    {
        var rawKey = "tamma_sk_neverrecognised";
        SeedLegacyKeyLookup(rawKey, returnedFromSha: null, returnedFromScrypt: null);

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    // ── Service-scope X-Tenant-Id (inherited path) ───────────────────

    [Test]
    public async Task ServiceKey_WithValidXTenantIdHeader_ResolvesTenant()
    {
        // Prefixed user-scope token but ApiKey row Scope='service' to
        // exercise the X-Tenant-Id branch — covers the inherited handler
        // surface end-to-end with the new prefix routing.
        var rawKey = ApiKeyPrefixGenerator.GenerateUserKey();
        var apiKey = BuildKey(scope: "service");
        // Service keys store an opaque ServiceName in OwnerId — overwrite
        // the auto-generated GUID body.
        apiKey.OwnerId = "ci-runner";
        SeedKeyLookup(rawKey, apiKey);

        var headerTid = Guid.NewGuid();
        _tenantRepo.Setup(r => r.GetByIdAsync(headerTid))
            .ReturnsAsync(new Tenant { Id = headerTid, Name = "T", Slug = "t" });

        var (result, ctx) = await RunWithHeadersAsync(
            $"Bearer {rawKey}",
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = headerTid.ToString(),
            });

        result.Succeeded.Should().BeTrue();
        ctx.Items[ApiKeyAuthHandler.TenantIdItemKey].Should().Be(headerTid);
    }

    [Test]
    public async Task ServiceKey_WithMalformedXTenantIdHeader_Returns401()
    {
        var rawKey = ApiKeyPrefixGenerator.GenerateUserKey();
        var apiKey = BuildKey(scope: "service");
        apiKey.OwnerId = "ci-runner";
        SeedKeyLookup(rawKey, apiKey);

        var (result, _) = await RunWithHeadersAsync(
            $"Bearer {rawKey}",
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "not-a-guid",
            });

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid X-Tenant-Id");
    }

    // ── Helper that mirrors RunAsync but allows extra headers ────────

    private async Task<(AuthenticateResult Result, HttpContext Context)> RunWithHeadersAsync(
        string authHeader,
        IDictionary<string, string> extraHeaders)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_apiKeyRepo.Object);
        services.AddSingleton(_userRepo.Object);
        services.AddSingleton(_instRepo.Object);
        services.AddSingleton(_tenantRepo.Object);
        services.AddSingleton(_resolver.Object);
        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeyAuthHandler.LegacyFallbackConfigKey] = "true",
            }).Build();

        var optionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(
            new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            sp,
            config);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/v1/something";
        ctx.Request.Headers["Authorization"] = authHeader;
        foreach (var kv in extraHeaders)
            ctx.Request.Headers[kv.Key] = kv.Value;

        var scheme = new AuthenticationScheme(
            "ApiKey", "ApiKey", typeof(ApiKeyAuthHandler));
        await handler.InitializeAsync(scheme, ctx);
        var result = await handler.AuthenticateAsync();
        return (result, ctx);
    }

    /// <summary>Trivial <see cref="IOptionsMonitor{T}"/> used so the handler
    /// can be constructed outside of the ASP.NET Core options pipeline.</summary>
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
