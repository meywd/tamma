using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Onboarding;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Onboarding;

/// <summary>
/// Story 31-9 — direct service tests for
/// <see cref="PlatformConnectService"/>. The picker UI submits via
/// <c>POST /api/onboarding/install</c>; that endpoint hands off to this
/// service.
///
/// <para>Uses Moq seams for the cross-cutting deps so the suite stays
/// lightweight (no Postgres container, no
/// <see cref="WebApplicationFactory{T}"/>). The test fakes track:
/// <list type="bullet">
///   <item><see cref="ISecretRevealService"/> — capture the credential
///         that goes to the cabinet so tests can assert the storage
///         path (not bypass).</item>
///   <item><see cref="ITenantPlatformInstallationRepository"/> — keyed
///         by id so the cross-tenant scope test can verify the
///         repository's <c>ListByTenantAsync</c> contract is honoured.</item>
///   <item><see cref="IGitPlatformDriverFactory"/> — observable probe
///         + a switch to throw on probe so the auth-fail path can be
///         exercised without a real network call.</item>
/// </list></para>
/// </summary>
[TestFixture]
public class PlatformConnectServiceTests
{
    private FakeInstallationRepo _installations = null!;
    private FakeRevealService _reveal = null!;
    private FakeEventEmitter _emitter = null!;
    private FakeDriverFactory _driverFactory = null!;
    private FakeTimeProvider _time = null!;
    private PlatformConnectService _service = null!;

    [SetUp]
    public void Setup()
    {
        _installations = new FakeInstallationRepo();
        _reveal = new FakeRevealService();
        _emitter = new FakeEventEmitter();
        _driverFactory = new FakeDriverFactory(PlatformKind.Gitea);
        _time = new FakeTimeProvider(
            DateTime.SpecifyKind(DateTime.Parse("2026-04-27T12:00:00Z"), DateTimeKind.Utc));

        var keyedRoot = new ServiceCollection();
        keyedRoot.AddKeyedSingleton<IGitPlatformDriverFactory>(
            PlatformKind.Gitea, _driverFactory);
        var keyedProvider = keyedRoot.BuildServiceProvider();

        _service = new PlatformConnectService(
            _installations,
            _reveal,
            keyedProvider,
            _emitter,
            _time,
            NullLogger<PlatformConnectService>.Instance);
    }

    [Test]
    public async Task Connect_WritesInstallationRowAndStoresSecret_OnHappyPath()
    {
        var tenantId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        var result = await _service.ConnectAsync(new PlatformConnectRequest(
            TenantId: tenantId,
            ActorUserId: actor,
            Kind: PlatformKind.Gitea,
            BaseUrl: "https://gitea.example.com",
            ExternalId: "ext-1",
            CredentialPlaintext: "test-token-123"));

        result.Succeeded.Should().BeTrue();
        result.InstallationId.Should().NotBeNull();
        result.Kind.Should().Be(PlatformKind.Gitea);
        result.BaseUrl.Should().Be("https://gitea.example.com");
        result.SecretName.Should().StartWith("gitea/install-");

        // Secret was written through the reveal service (Epic 29
        // gate). The plaintext is exactly what we passed in — proves
        // the connect service does not transform the credential before
        // handing it off.
        _reveal.LastInitialPlaintext.Should().Be("test-token-123");
        _reveal.LastTenantId.Should().Be(tenantId);
        _reveal.LastScope.Should().Be(SecretScope.Tenant);
        _reveal.LastPurpose.Should().Be(SecretPurpose.ApiKey);

        // Installation row inserted with correct shape.
        var row = _installations.Created.Should().ContainSingle().Subject;
        row.TenantId.Should().Be(tenantId);
        row.PlatformKind.Should().Be("gitea");
        row.BaseUrl.Should().Be("https://gitea.example.com");
        row.InstallationExternalId.Should().Be("ext-1");
        row.CredentialSecretScope.Should().Be("tenant");
        row.CredentialSecretName.Should().Be(result.SecretName);
        row.Status.Should().Be("connected");
        row.IsPrimary.Should().BeTrue();

        // Driver factory probed our credential — proves the auth dry
        // run actually goes through the production driver path.
        _driverFactory.LastCredentialPlaintext.Should().Be("test-token-123");
        _driverFactory.LastInstallationBaseUrl.Should().Be("https://gitea.example.com");

        // Connected event was emitted.
        _emitter.ConnectedEvents.Should().ContainSingle()
            .Which.TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task Connect_ReturnsDriverUnavailable_ForKindWithoutFactory()
    {
        var result = await _service.ConnectAsync(new PlatformConnectRequest(
            TenantId: Guid.NewGuid(),
            ActorUserId: Guid.NewGuid(),
            Kind: PlatformKind.Bitbucket,
            BaseUrl: "https://bitbucket.org",
            ExternalId: null,
            CredentialPlaintext: "token"));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("driver_unavailable");
        // No secret should have been written + no row created.
        _reveal.LastInitialPlaintext.Should().BeNull();
        _installations.Created.Should().BeEmpty();
    }

    [Test]
    public async Task Connect_ReturnsAuthProbeFailed_AndDoesNotPersistRow_OnProbeException()
    {
        _driverFactory.ThrowOnCreate = new InvalidOperationException("401 unauthorized");

        var result = await _service.ConnectAsync(new PlatformConnectRequest(
            TenantId: Guid.NewGuid(),
            ActorUserId: Guid.NewGuid(),
            Kind: PlatformKind.Gitea,
            BaseUrl: "https://gitea.example.com",
            ExternalId: null,
            CredentialPlaintext: "bad-token"));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("auth_probe_failed");

        // No installation row — caller can retry. The secret IS
        // written (via the reveal-once pattern) but no row points at
        // it; an out-of-band cleanup or the operator's next try will
        // mint a new secret + replace it.
        _installations.Created.Should().BeEmpty();
    }

    [Test]
    public async Task Connect_FailsClosed_WhenDriverCannotEnumerateRepos()
    {
        // Epic 31 P5 M1 probe strictness: a driver without the
        // ListAccessibleRepos capability cannot prove the credential
        // authenticates — connect must FAIL rather than persist a
        // 'connected' row on an unverifiable credential (the empty-as-
        // success class GitHub's P1 fix closed for one kind).
        _driverFactory.DriverCapabilities = new HashSet<PlatformCapability>();

        var result = await _service.ConnectAsync(new PlatformConnectRequest(
            TenantId: Guid.NewGuid(),
            ActorUserId: Guid.NewGuid(),
            Kind: PlatformKind.Gitea,
            BaseUrl: "https://gitea.example.com",
            ExternalId: null,
            CredentialPlaintext: "unverifiable-token"));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("auth_probe_unsupported");
        _installations.Created.Should().BeEmpty();
        _emitter.ConnectedEvents.Should().BeEmpty();
    }

    [Test]
    public async Task Connect_RejectsInvalidBody()
    {
        var result = await _service.ConnectAsync(new PlatformConnectRequest(
            TenantId: Guid.NewGuid(),
            ActorUserId: Guid.NewGuid(),
            Kind: PlatformKind.Gitea,
            BaseUrl: "",
            ExternalId: null,
            CredentialPlaintext: "tok"));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_base_url");
    }

    [Test]
    public async Task Connect_RejectsMissingCredential()
    {
        var result = await _service.ConnectAsync(new PlatformConnectRequest(
            TenantId: Guid.NewGuid(),
            ActorUserId: Guid.NewGuid(),
            Kind: PlatformKind.Gitea,
            BaseUrl: "https://gitea.example.com",
            ExternalId: null,
            CredentialPlaintext: ""));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_credential");
    }

    [Test]
    public async Task ListForTenant_ScopesToCallerTenantOnly()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await _service.ConnectAsync(new PlatformConnectRequest(
            tenantA, Guid.NewGuid(), PlatformKind.Gitea,
            "https://gitea-a.example.com", null, "tok-a"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _service.ConnectAsync(new PlatformConnectRequest(
            tenantB, Guid.NewGuid(), PlatformKind.Gitea,
            "https://gitea-b.example.com", null, "tok-b"));

        var listA = await _service.ListForTenantAsync(tenantA);
        var listB = await _service.ListForTenantAsync(tenantB);

        listA.Should().HaveCount(1);
        listA[0].BaseUrl.Should().Be("https://gitea-a.example.com");
        listB.Should().HaveCount(1);
        listB[0].BaseUrl.Should().Be("https://gitea-b.example.com");
    }

    /// <summary>
    /// Inline TimeProvider stub mirroring the Story 1.5-37 test pattern;
    /// kept inline so the test project's package surface stays narrow.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTime initialUtc)
        {
            _now = new DateTimeOffset(DateTime.SpecifyKind(initialUtc, DateTimeKind.Utc));
        }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    /// <summary>
    /// Test-double <see cref="ISecretRevealService"/>. Captures the
    /// most recent <see cref="IssueCreateAsync"/> arguments so tests
    /// can assert the cabinet write happened with the correct shape.
    /// </summary>
    private sealed class FakeRevealService : ISecretRevealService
    {
        public string? LastName { get; private set; }
        public SecretScope? LastScope { get; private set; }
        public Guid? LastTenantId { get; private set; }
        public SecretPurpose? LastPurpose { get; private set; }
        public string? LastInitialPlaintext { get; private set; }
        public Guid? LastOwnerUserId { get; private set; }

        public Task<RevealTokenIssueResult> IssueCreateAsync(
            string name,
            SecretScope scope,
            Guid? tenantId,
            SecretPurpose purpose,
            string initialPlaintext,
            IReadOnlyList<ConsumerRef>? consumerRefs,
            Guid ownerUserId,
            RotationSchedule? rotationSchedule,
            CancellationToken ct = default)
        {
            LastName = name;
            LastScope = scope;
            LastTenantId = tenantId;
            LastPurpose = purpose;
            LastInitialPlaintext = initialPlaintext;
            LastOwnerUserId = ownerUserId;

            var metadata = new SecretMetadata(
                Id: Guid.NewGuid(),
                Name: name,
                Scope: scope,
                TenantId: tenantId,
                Purpose: purpose,
                ConsumerRefs: consumerRefs ?? Array.Empty<ConsumerRef>(),
                OwnerUserId: ownerUserId,
                RotationSchedule: rotationSchedule ?? RotationSchedule.None,
                LastRotatedAt: null,
                NextRotationDueAt: null,
                ActiveVersionNumber: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);
            return Task.FromResult(new RevealTokenIssueResult(
                Metadata: metadata,
                RevealToken: "test-reveal-token",
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(60)));
        }

        public Task<RevealTokenIssueResult> IssueRotateAsync(
            Guid secretId,
            string newPlaintext,
            Guid actorUserId,
            CancellationToken ct = default) =>
            throw new NotImplementedException(
                "FakeRevealService does not exercise rotation in these tests.");

        public Task<RevealTokenConsumeResult> ConsumeAsync(
            string rawToken, RevealCallerContext caller,
            CancellationToken ct = default) =>
            throw new NotImplementedException(
                "FakeRevealService does not exercise consume in these tests.");

        public Task<int> SweepExpiredAsync(CancellationToken ct = default) =>
            throw new NotImplementedException(
                "FakeRevealService does not exercise sweep in these tests.");
    }

    /// <summary>
    /// In-memory <see cref="ITenantPlatformInstallationRepository"/>.
    /// Mirrors the EF repository's tenant-scoped query semantics so
    /// tests can assert cross-tenant isolation without a Postgres
    /// container.
    /// </summary>
    private sealed class FakeInstallationRepo : ITenantPlatformInstallationRepository
    {
        public List<TenantPlatformInstallation> Created { get; } = new();

        public Task<TenantPlatformInstallation> CreateAsync(
            TenantPlatformInstallation installation,
            CancellationToken ct = default)
        {
            Created.Add(installation);
            return Task.FromResult(installation);
        }

        public Task<TenantPlatformInstallation?> GetByTenantPrimaryAsync(
            Guid tenantId, CancellationToken ct = default)
        {
            var rows = Created.Where(r => r.TenantId == tenantId && r.DeletedAt == null).ToList();
            return Task.FromResult<TenantPlatformInstallation?>(
                rows.FirstOrDefault(r => r.IsPrimary)
                ?? (rows.Count == 1 ? rows[0] : null));
        }

        public Task<TenantPlatformInstallation?> GetByTenantKindAsync(
            Guid tenantId, string platformKind, CancellationToken ct = default)
        {
            var match = Created.FirstOrDefault(r =>
                r.TenantId == tenantId
                && r.PlatformKind == platformKind
                && r.DeletedAt == null);
            return Task.FromResult<TenantPlatformInstallation?>(match);
        }

        public Task<TenantPlatformInstallation?> GetByIdAsync(
            Guid id, CancellationToken ct = default)
        {
            var match = Created.FirstOrDefault(r => r.Id == id && r.DeletedAt == null);
            return Task.FromResult<TenantPlatformInstallation?>(match);
        }

        public Task<TenantPlatformInstallation?> GetByExternalIdAsync(
            string platformKind, string installationExternalId,
            CancellationToken ct = default)
        {
            var match = Created.FirstOrDefault(r =>
                r.PlatformKind == platformKind
                && r.InstallationExternalId == installationExternalId
                && r.DeletedAt == null);
            return Task.FromResult<TenantPlatformInstallation?>(match);
        }

        public Task<IReadOnlyList<TenantPlatformInstallation>> ListByTenantAsync(
            Guid tenantId, CancellationToken ct = default)
        {
            IReadOnlyList<TenantPlatformInstallation> rows = Created
                .Where(r => r.TenantId == tenantId && r.DeletedAt == null)
                .ToList();
            return Task.FromResult(rows);
        }

        public Task<TenantPlatformInstallation> UpdateAsync(
            TenantPlatformInstallation installation,
            CancellationToken ct = default) =>
            throw new NotImplementedException("Not used in 31-9 tests.");

        public Task SoftDeleteAsync(Guid id, CancellationToken ct = default) =>
            throw new NotImplementedException("Not used in 31-9 tests.");

        public Task RestoreAsync(Guid id, CancellationToken ct = default) =>
            throw new NotImplementedException("Not used in 31-9 tests.");
    }

    /// <summary>
    /// Captures emitted events without writing to the platform event
    /// log so tests don't need a Postgres container.
    /// </summary>
    private sealed class FakeEventEmitter : IPlatformInstallationEventEmitter
    {
        public List<EmittedEvent> ConnectedEvents { get; } = new();

        public Task EmitConnectedAsync(
            Guid tenantId, PlatformKind kind, Guid installationRowId,
            string? installationExternalId, Guid? actorUserId,
            CancellationToken ct = default)
        {
            ConnectedEvents.Add(new EmittedEvent(
                tenantId, kind, installationRowId,
                installationExternalId, actorUserId));
            return Task.CompletedTask;
        }

        public Task EmitDisconnectedAsync(
            Guid tenantId, PlatformKind kind, Guid installationRowId,
            string? installationExternalId, Guid? actorUserId,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task EmitCredentialRotatedAsync(
            Guid tenantId, PlatformKind kind, Guid installationRowId,
            Guid? actorUserId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public sealed record EmittedEvent(
            Guid TenantId, PlatformKind Kind, Guid InstallationRowId,
            string? ExternalId, Guid? ActorUserId);
    }

    /// <summary>
    /// Test double for <see cref="IGitPlatformDriverFactory"/>.
    /// Records the credential plaintext + base url it's handed (so
    /// tests can assert the secret-store roundtrip and base url
    /// passthrough) and exposes a switch to throw on probe. The
    /// returned driver advertises <see cref="PlatformCapability.ListAccessibleRepos"/>
    /// by default — the P5 M1 probe strictness fails connect for a
    /// driver that cannot enumerate repos, and the happy-path tests
    /// model a probe-capable driver.
    /// </summary>
    private sealed class FakeDriverFactory : IGitPlatformDriverFactory
    {
        public FakeDriverFactory(PlatformKind kind) => Kind = kind;

        public PlatformKind Kind { get; }
        public string? LastCredentialPlaintext { get; private set; }
        public string? LastInstallationBaseUrl { get; private set; }
        public Exception? ThrowOnCreate { get; set; }
        public IReadOnlySet<PlatformCapability> DriverCapabilities { get; set; } =
            new HashSet<PlatformCapability> { PlatformCapability.ListAccessibleRepos };

        public Task<IGitPlatformDriver> CreateAsync(
            PlatformInstallation installation,
            string credentialPlaintext,
            CancellationToken ct = default)
        {
            LastCredentialPlaintext = credentialPlaintext;
            LastInstallationBaseUrl = installation.BaseUrl;
            if (ThrowOnCreate is not null) throw ThrowOnCreate;
            IGitPlatformDriver driver = new FakeDriver
            {
                Kind = Kind,
                Capabilities = DriverCapabilities,
            };
            return Task.FromResult(driver);
        }
    }

    /// <summary>Minimal driver: null-object client (empty accessible-repos
    /// enumeration = successful auth handshake with zero repos) + a
    /// configurable capability set.</summary>
    private sealed class FakeDriver : IGitPlatformDriver
    {
        public PlatformKind Kind { get; init; } = PlatformKind.Gitea;
        public IGitPlatformClient Client { get; init; } = NullGitPlatformDriver.Instance.Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; init; } =
            new HashSet<PlatformCapability>();
    }
}
