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
        DateTime? revokedAt = null,
        string keyHash = "stub",
        string keyPrefix = "tamma_sk_xx")
    {
        return new ApiKey
        {
            Id = id ?? Guid.NewGuid(),
            Scope = scope,
            OwnerId = (ownerId ?? Guid.NewGuid()).ToString(),
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Label = "test",
            Permissions = Array.Empty<string>(),
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
        };
    }

    /// <summary>
    /// Wire a key-lookup mock for <paramref name="rawKey"/>. Story 28-7
    /// deferred-item path uses <see cref="ApiKeyHasher.Verify"/> which needs
    /// <paramref name="returned"/>.KeyHash to actually match the raw key;
    /// this helper rewrites KeyHash to the SHA-256 of the raw key so Verify
    /// passes for the legacy-shape fixtures.
    /// </summary>
    private void SeedKeyLookup(string rawKey, ApiKey? returned)
    {
        var sha = ApiKeyHasher.Hash(rawKey);
        if (returned is not null)
            returned.KeyHash = sha;
        _apiKeyRepo.Setup(r => r.GetByHashAsync(sha))
            .ReturnsAsync(returned);
        // ListValidByScopeAsync is used by the legacy fallback path to scan for
        // Argon2-format rows; Strict mock needs a default empty list.
        _apiKeyRepo.Setup(r => r.ListValidByScopeAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ApiKey>());
    }

    private void SeedLegacyKeyLookup(string rawKey, ApiKey? returnedFromSha, ApiKey? returnedFromScrypt)
    {
        var sha = ApiKeyHasher.Hash(rawKey);
        var scrypt = ApiKeyHasher.LegacyScryptHash(rawKey);
        if (returnedFromSha is not null)
            returnedFromSha.KeyHash = sha;
        if (returnedFromScrypt is not null)
            returnedFromScrypt.KeyHash = scrypt;
        _apiKeyRepo.Setup(r => r.GetByHashAsync(sha)).ReturnsAsync(returnedFromSha);
        _apiKeyRepo.Setup(r => r.GetByHashAsync(scrypt)).ReturnsAsync(returnedFromScrypt);
        _apiKeyRepo.Setup(r => r.ListValidByScopeAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ApiKey>());
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
    public async Task TenantPrefixedKey_TenantNotFound_Returns404()
    {
        // H7 — tenant-not-found in the prefix-routing path now writes a
        // structured 404 response (Doc 04 §8.1) directly to the
        // response stream, then fails the auth result. The failure
        // message changed from the historical "Invalid API key" to
        // "Tenant not found" so logs distinguish the two failure modes.
        var tid = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(scope: "user", tenantId: tid);
        SeedKeyLookup(rawKey, apiKey);

        _resolver.Setup(r => r.GetDataSourceAsync(tid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TenantNotFoundException(tid));

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Tenant not found");
    }

    [Test]
    public async Task TenantPrefixedKey_TenantSuspended_Returns503_WithStatusCode()
    {
        // H7 — non-active tenant now surfaces as the proper Doc 04 §8.1
        // status (e.g. 503 for suspended/provisioning, 410 for deleted)
        // instead of a generic 401. Auth still fails so the request
        // pipeline short-circuits.
        var tid = Guid.NewGuid();
        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var apiKey = BuildKey(scope: "user", tenantId: tid);
        SeedKeyLookup(rawKey, apiKey);

        _resolver.Setup(r => r.GetDataSourceAsync(tid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TenantNotProvisionedException(tid, "provisioning"));

        var (result, _) = await RunAsync($"Bearer {rawKey}");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Tenant not in active state");
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

    // ── Installation-scope keys, post-Argon2-rehash (2026-08-18) ─────
    //
    // An installation key is minted un-prefixed, so it authenticates on the
    // LEGACY path. Its FIRST successful request rehashes the row to per-key-
    // salted Argon2, after which no hash-equality lookup can ever find it
    // again and the only route left is ResolveByVerify's KeyPrefix-equality
    // scan. The issuance sites stored a 16-char slice while that scan compares
    // ApiKeyHasher.Prefix (12), so every installation key worked exactly once
    // and then 401'd forever. These two tests are the before/after.

    private void SeedArgon2PrefixScan(ApiKey installationRow)
    {
        _apiKeyRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>()))
            .ReturnsAsync((ApiKey?)null);
        _apiKeyRepo.Setup(r => r.ListValidByScopeAsync("service"))
            .ReturnsAsync(new List<ApiKey>());
        _apiKeyRepo.Setup(r => r.ListValidByScopeAsync("user"))
            .ReturnsAsync(new List<ApiKey>());
        _apiKeyRepo.Setup(r => r.ListValidByScopeAsync("installation"))
            .ReturnsAsync(new List<ApiKey> { installationRow });
    }

    /// <summary>
    /// The installation ENTITY id both issuance sites write into OwnerId. Using a real
    /// Guid here matters: this fixture used to write "12345" — the GitHub installation id
    /// shape, which production never stores — and the handler's ticket branch parsed
    /// OwnerId as a long, so the mismatch cancelled out and the suite went green on a
    /// path no real key could take.
    /// </summary>
    private static readonly Guid InstallationEntityId = Guid.NewGuid();

    private const long GitHubInstallationId = 12345L;

    /// <summary>Make the entity lookup resolve, as it does against a real database.</summary>
    private void SeedInstallationEntity(DateTime? suspendedAt = null) =>
        _instRepo.Setup(r => r.GetByEntityIdAsync(InstallationEntityId))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = InstallationEntityId,
                InstallationId = GitHubInstallationId,
                AccountLogin = "acme",
                SuspendedAt = suspendedAt,
            });

    private static ApiKey BuildInstallationRow(string rawKey, string storedPrefix) => new()
    {
        Id = Guid.NewGuid(),
        Scope = "installation",
        OwnerId = InstallationEntityId.ToString(),
        KeyHash = ApiKeyHasher.HashArgon2(rawKey),
        KeyPrefix = storedPrefix,
        Label = "installation-key",
        Permissions = Array.Empty<string>(),
        TenantId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task InstallationKey_WithCanonicalStoredPrefix_StillAuthenticatesAfterRehash()
    {
        var rawKey = ApiKeyHasher.NewKey();
        SeedArgon2PrefixScan(BuildInstallationRow(rawKey, ApiKeyHasher.Prefix(rawKey)));
        SeedInstallationEntity();

        var (result, ctx) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeTrue(
            "the stored KeyPrefix is exactly what ResolveByVerify computes from the raw key");

        // The prefix fix alone was not enough: the ticket branch resolved OwnerId as a
        // long, so a Guid OwnerId — the only shape production writes — failed here with
        // "Invalid API key scope" AFTER the lookup succeeded. Asserting the resolved
        // principal, not just Succeeded, is what makes this test see that.
        ctx.GetAuthPrincipal().Should().BeOfType<InstallationAuthPrincipal>()
            .Which.InstallationId.Should().Be(GitHubInstallationId,
                "the principal carries the GitHub installation id, resolved from the entity row");
    }

    [Test]
    public async Task InstallationKey_WhoseInstallationRowIsGone_IsRefused()
    {
        // The entity lookup is deliberately NOT seeded. This used to authenticate: the
        // branch read `inst?.SuspendedAt`, so a null installation skipped the suspension
        // check and still built a valid ticket.
        var rawKey = ApiKeyHasher.NewKey();
        SeedArgon2PrefixScan(BuildInstallationRow(rawKey, ApiKeyHasher.Prefix(rawKey)));

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key scope");
    }

    [Test]
    public async Task InstallationKey_ForASuspendedInstallation_IsRefused()
    {
        var rawKey = ApiKeyHasher.NewKey();
        SeedArgon2PrefixScan(BuildInstallationRow(rawKey, ApiKeyHasher.Prefix(rawKey)));
        SeedInstallationEntity(suspendedAt: DateTime.UtcNow);

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Installation is suspended");
    }

    [Test]
    public async Task InstallationKey_WithSixteenCharStoredPrefix_CanNeverAuthenticate()
    {
        // The shipped issuance shape until 2026-08-18. Kept as the regression
        // pin: if the stored prefix ever drifts from ApiKeyHasher.Prefix again,
        // the test above goes red and this one explains why.
        var rawKey = ApiKeyHasher.NewKey();
        SeedArgon2PrefixScan(BuildInstallationRow(rawKey, rawKey[..16]));

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    [Test]
    public async Task InstallationKey_InsideItsRotationGraceWindow_StillAuthenticates()
    {
        // RotateAsync stamps RevokedAt = now + 24h so dependent services can roll over
        // without an outage — the TAMMA_API_KEY sitting in every customer repo's Actions
        // secrets is exactly that case. But the candidate scan listed only RevokedAt == null
        // rows, so a rotated key vanished from the only lookup path a used key has left
        // (its first auth rehashes the row to per-key-salted Argon2, after which no hash
        // lookup can find it). The documented grace period was unreachable in practice.
        var rawKey = ApiKeyHasher.NewKey();
        var row = BuildInstallationRow(rawKey, ApiKeyHasher.Prefix(rawKey));
        row.RevokedAt = DateTime.UtcNow.AddHours(24);
        SeedArgon2PrefixScan(row);
        SeedInstallationEntity();

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeTrue(
            "a key whose RevokedAt is still in the future is inside its rotation grace window");
    }

    [Test]
    public async Task InstallationKey_PastItsRevocationMoment_IsRefused()
    {
        var rawKey = ApiKeyHasher.NewKey();
        var row = BuildInstallationRow(rawKey, ApiKeyHasher.Prefix(rawKey));
        row.RevokedAt = DateTime.UtcNow.AddHours(-1);
        SeedArgon2PrefixScan(row);
        SeedInstallationEntity();

        var (result, _) = await RunAsync($"Bearer {rawKey}", allowLegacy: true);

        result.Succeeded.Should().BeFalse("the grace window has closed");
    }

    [Test]
    public void NewKey_NeverCollidesWithAScopeMarker()
    {
        // base64url includes '_', so an un-guarded random body can start "u_",
        // "t_" or "pl_"; the parser then reads it as a Story-28-7 scope marker
        // and the key routes to the prefixed path it has no row for.
        for (var i = 0; i < 20_000; i++)
        {
            ApiKeyPrefixParser.TryParse(ApiKeyHasher.NewKey(), out var parsed)
                .Should().BeTrue();
            parsed!.Scope.Should().Be(ApiKeyScope.Legacy,
                "an un-prefixed key must always parse as legacy, never as a scoped key");
        }
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
