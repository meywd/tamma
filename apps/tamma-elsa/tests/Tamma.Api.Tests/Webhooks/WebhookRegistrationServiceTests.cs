using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Api.Services.Webhooks.Registration;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Webhooks;

/// <summary>
/// Epic 31 P4 M3 — <c>git.webhook.register</c> goes live: the registration
/// caller, its secret plumbing, and every §4 degradation path.
///
/// <para><b>Red-first claim.</b> Before this milestone
/// <c>IGitPlatformClient.RegisterWebhookAsync</c> had ZERO production callers
/// (the catalog row's RESERVED note said exactly that) — connecting an
/// installation left NO hook on any repo. The happy-path test fails on the
/// pre-milestone tree because the service does not exist.</para>
/// </summary>
[TestFixture]
public class WebhookRegistrationServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private Mock<ITenantPlatformInstallationRepository> _installations = null!;
    private Mock<ISecretRevealService> _secrets = null!;
    private RecordingEventRepository _events = null!;
    private Mock<IGitPlatformClient> _client = null!;

    [SetUp]
    public void SetUp()
    {
        _installations = new Mock<ITenantPlatformInstallationRepository>();
        _installations
            .Setup(r => r.UpdateAsync(It.IsAny<TenantPlatformInstallation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantPlatformInstallation r, CancellationToken _) => r);
        _secrets = new Mock<ISecretRevealService>();
        _secrets
            .Setup(s => s.IssueCreateAsync(
                It.IsAny<string>(), It.IsAny<SecretScope>(), It.IsAny<Guid?>(),
                It.IsAny<SecretPurpose>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConsumerRef>?>(), It.IsAny<Guid>(),
                It.IsAny<RotationSchedule?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, SecretScope scope, Guid? tenantId, SecretPurpose purpose,
                string _, IReadOnlyList<ConsumerRef>? __, Guid owner, RotationSchedule? ___, CancellationToken ____) =>
                new RevealTokenIssueResult(
                    new SecretMetadata(
                        Guid.NewGuid(), name, scope, tenantId, purpose,
                        Array.Empty<ConsumerRef>(), owner, RotationSchedule.None,
                        null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        _events = new RecordingEventRepository();
        _client = new Mock<IGitPlatformClient>();
    }

    private WebhookRegistrationService Build(string? publicBaseUrl, bool withSecretStore = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [WebhookRegistrationService.PublicBaseUrlConfigKey] = publicBaseUrl,
            }).Build();
        return new WebhookRegistrationService(
            config,
            _installations.Object,
            _events,
            TimeProvider.System,
            NullLogger<WebhookRegistrationService>.Instance,
            withSecretStore ? _secrets.Object : null);
    }

    private FakeDriver Driver(
        PlatformKind kind = PlatformKind.Gitea,
        bool webhookCapable = true,
        params string[] repos)
    {
        var caps = new HashSet<PlatformCapability>();
        if (webhookCapable) caps.Add(PlatformCapability.WebhookHmac);
        _client
            .Setup(c => c.ListAccessibleReposAsync(It.IsAny<CancellationToken>()))
            .Returns(ReposOf(repos));
        return new FakeDriver(kind, _client.Object, caps);
    }

    private static async IAsyncEnumerable<PModels.Repo> ReposOf(string[] slugs)
    {
        foreach (var slug in slugs)
        {
            var parts = slug.Split('/');
            yield return new PModels.Repo(
                Host: "gitea.example.com",
                Owner: parts[0],
                Name: parts[1],
                DefaultBranch: "main",
                IsPrivate: false,
                Description: null,
                CloneUrl: $"https://gitea.example.com/{slug}.git",
                HtmlUrl: $"https://gitea.example.com/{slug}");
        }
        await Task.CompletedTask;
    }

    private void RegistrationSucceeds() => _client
        .Setup(c => c.RegisterWebhookAsync(
            It.IsAny<PModels.RegisterWebhookRequest>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(PlatformResult<PModels.WebhookRegistration>.FromOk(
            new PModels.WebhookRegistration("hook-1", "https://t/api/webhooks/gitea", new[] { "push" }, true)));

    private TenantPlatformInstallation Row() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        PlatformKind = "gitea",
        BaseUrl = "https://gitea.example.com",
        Status = "connected",
    };

    // ================================================================
    // Happy path — the acceptance: "connecting a Gitea installation
    // leaves a live hook on the repo".
    // ================================================================

    [Test]
    public async Task Connect_MintsSecret_StampsRow_RegistersHook_EmitsSuccess()
    {
        RegistrationSucceeds();
        var row = Row();
        var sut = Build("https://tamma.example.com");

        var outcome = await sut.RegisterForInstallationAsync(
            Driver(repos: new[] { "acme/widgets" }), row, Actor);

        outcome.Status.Should().Be("registered");
        outcome.ReposRegistered.Should().Be(1);

        // The secret ref landed on the row — exactly where the 31-7
        // receiver's WebhookSecretResolver reads it back.
        row.WebhookSecretScope.Should().Be("tenant");
        row.WebhookSecretName.Should().StartWith("gitea/webhook-");
        _installations.Verify(r => r.UpdateAsync(row, It.IsAny<CancellationToken>()), Times.Once);

        // The hook carries the callback URL computed from Tamma:PublicBaseUrl
        // and the SAME secret that went into the cabinet.
        _client.Verify(c => c.RegisterWebhookAsync(
            It.Is<PModels.RegisterWebhookRequest>(r =>
                r.DeliveryUrl == "https://tamma.example.com/api/webhooks/gitea"
                && r.Owner == "acme" && r.RepoName == "widgets"
                && !string.IsNullOrEmpty(r.Secret)),
            It.IsAny<CancellationToken>()), Times.Once);
        _secrets.Verify(s => s.IssueCreateAsync(
            It.Is<string>(n => n.StartsWith("gitea/webhook-")),
            SecretScope.Tenant, Tenant, SecretPurpose.SigningKey,
            It.IsAny<string>(), null, Actor, null, It.IsAny<CancellationToken>()), Times.Once);

        _events.Appended.Should().ContainSingle()
            .Which.Type.Should().Be(WebhookRegistrationService.SuccessEventType);
    }

    [Test]
    public async Task PerRepoFailure_DegradesToRecordedPartial_NeverThrows()
    {
        _client
            .SetupSequence(c => c.RegisterWebhookAsync(
                It.IsAny<PModels.RegisterWebhookRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<PModels.WebhookRegistration>.FromOk(
                new PModels.WebhookRegistration("hook-1", "u", new[] { "push" }, true)))
            .ReturnsAsync(PlatformResult<PModels.WebhookRegistration>.FromError(
                new PlatformError.PermissionDenied()));

        var outcome = await Build("https://tamma.example.com").RegisterForInstallationAsync(
            Driver(repos: new[] { "acme/a", "acme/b" }), Row(), Actor);

        outcome.Status.Should().Be("partial");
        outcome.ReposRegistered.Should().Be(1);
        outcome.ReposFailed.Should().Be(1);
        _events.Appended.Should().ContainSingle()
            .Which.Type.Should().Be(WebhookRegistrationService.PartialEventType);
    }

    // ================================================================
    // §4 degradation — every cannot-proceed branch is the ALTERNATIVE
    // STEP: record manual-registration-needed + audit, never a failure.
    // ================================================================

    [Test]
    public async Task NoPublicBaseUrl_SkipsWithAudit_ManualRegistrationNeeded()
    {
        var outcome = await Build(publicBaseUrl: null).RegisterForInstallationAsync(
            Driver(repos: new[] { "acme/widgets" }), Row(), Actor);

        outcome.Status.Should().Be("skipped");
        outcome.SkipReason.Should().Be("no_public_base_url");
        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(WebhookRegistrationService.SkippedEventType);
        evt.Data.Should().Contain("manualRegistrationNeeded");
        _client.Verify(c => c.RegisterWebhookAsync(
            It.IsAny<PModels.RegisterWebhookRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CapabilityUnsupported_SkipsWithAudit_TheOwnerMechanism()
    {
        // §4: the is-supported check runs BEFORE the action; unsupported takes
        // the defined alternative step (record + audit), never a hard failure.
        var outcome = await Build("https://tamma.example.com").RegisterForInstallationAsync(
            Driver(webhookCapable: false, repos: new[] { "acme/widgets" }), Row(), Actor);

        outcome.Status.Should().Be("skipped");
        outcome.SkipReason.Should().Be("capability_unsupported");
        _events.Appended.Should().ContainSingle()
            .Which.Type.Should().Be(WebhookRegistrationService.SkippedEventType);
    }

    [Test]
    public async Task NoSecretStore_SkipsWithAudit()
    {
        var outcome = await Build("https://tamma.example.com", withSecretStore: false)
            .RegisterForInstallationAsync(Driver(repos: new[] { "acme/widgets" }), Row(), Actor);

        outcome.Status.Should().Be("skipped");
        outcome.SkipReason.Should().Be("secret_store_unavailable");
    }

    [Test]
    public async Task ConfigTier_NoConfiguredSecret_SkipsWithAudit()
    {
        // Single-user path: the receiver verifies config-tier deliveries
        // against Webhooks:Secrets:{kind}; registering with a minted secret
        // the receiver cannot see would break verification, so an unset value
        // is the documented manual path.
        var outcome = await Build("https://tamma.example.com").RegisterWithSecretAsync(
            Driver(repos: new[] { "acme/widgets" }), PlatformKind.Gitea,
            webhookSecret: "", tenantId: null);

        outcome.Status.Should().Be("skipped");
        outcome.SkipReason.Should().Be("no_webhook_secret_configured");
    }

    [Test]
    public async Task ConfigTier_WithSecret_RegistersUsingTheConfiguredValue()
    {
        RegistrationSucceeds();

        var outcome = await Build("https://tamma.example.com").RegisterWithSecretAsync(
            Driver(repos: new[] { "acme/widgets" }), PlatformKind.Gitea,
            webhookSecret: "config-secret", tenantId: null);

        outcome.Status.Should().Be("registered");
        _client.Verify(c => c.RegisterWebhookAsync(
            It.Is<PModels.RegisterWebhookRequest>(r => r.Secret == "config-secret"),
            It.IsAny<CancellationToken>()), Times.Once);
        _secrets.Verify(s => s.IssueCreateAsync(
            It.IsAny<string>(), It.IsAny<SecretScope>(), It.IsAny<Guid?>(),
            It.IsAny<SecretPurpose>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<ConsumerRef>?>(), It.IsAny<Guid>(),
            It.IsAny<RotationSchedule?>(), It.IsAny<CancellationToken>()), Times.Never,
            "the config-tier path never mints a secret the receiver could not read back");
    }

    [Test]
    public async Task GitLab_EventVocabulary_IsThePipelineShape()
    {
        WebhookRegistrationService.EventsFor(PlatformKind.GitLab)
            .Should().BeEquivalentTo("push", "merge_request", "issue", "pipeline");
        WebhookRegistrationService.EventsFor(PlatformKind.Gitea)
            .Should().BeEquivalentTo("push", "pull_request", "issues", "workflow_run");
        await Task.CompletedTask;
    }

    // ================================================================
    // Plumbing
    // ================================================================

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public FakeDriver(PlatformKind kind, IGitPlatformClient client, IReadOnlySet<PlatformCapability> caps)
        {
            Kind = kind;
            Client = client;
            Capabilities = caps;
        }

        public PlatformKind Kind { get; }
        public IGitPlatformClient Client { get; }
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
