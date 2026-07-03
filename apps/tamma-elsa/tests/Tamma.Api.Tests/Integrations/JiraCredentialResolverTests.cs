using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Integrations;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Tests.Integrations;

/// <summary>
/// JIRA BYOK resolver — tenant→system→fail-loud, mirroring git BYOK's
/// GitTokenResolver. Pins: tenant cabinet bundle wins (SaaS); single-user config
/// is the system tier; SaaS with no bundle fails loud (NO config fallback); a
/// malformed stored bundle is treated as absent; Invalidate re-reads.
/// </summary>
[TestFixture]
public class JiraCredentialResolverTests
{
    // Obvious fakes — never a real token/secret literal.
    private const string FakeToken = "fake-jira-api-token-value";
    private const string BaseUrl = "https://jira.example.com";
    private const string Email = "bot@example.com";
    private static readonly Guid Tenant = Guid.NewGuid();

    private FakeCabinetReader _cabinet = null!;

    [SetUp]
    public void SetUp() => _cabinet = new FakeCabinetReader();

    private JiraCredentialResolver Build(TammaMode mode, IConfiguration? config = null)
    {
        var modeProvider = new Mock<ITammaModeProvider>();
        modeProvider.SetupGet(m => m.Mode).Returns(mode);
        return new JiraCredentialResolver(
            _cabinet, config ?? Empty(), modeProvider.Object,
            NullLogger<JiraCredentialResolver>.Instance);
    }

    private static IConfiguration Empty() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration JiraConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jira:BaseUrl"] = "https://sys.example.com",
            ["Jira:Email"] = "sys@example.com",
            ["Jira:ApiToken"] = "fake-system-jira-token",
        }).Build();

    private static string Bundle(string baseUrl = BaseUrl, string email = Email, string token = FakeToken) =>
        JiraCredentialCodec.Serialize(new JiraCredential(baseUrl, email, token));

    [Test]
    public async Task Saas_TenantBundlePresent_ResolvesTenantTier()
    {
        _cabinet.Set(Tenant, IntegrationCabinetNames.JiraConfig, Bundle());
        var sut = Build(TammaMode.SaaS);

        var res = await sut.ResolveAsync(Tenant);

        res.Should().NotBeNull();
        res!.Source.Should().Be(IntegrationCredentialSource.Tenant);
        res.Credential.BaseUrl.Should().Be(BaseUrl);
        res.Credential.Email.Should().Be(Email);
        res.Credential.ApiToken.Should().Be(FakeToken);
    }

    [Test]
    public async Task Saas_NoBundle_FailsLoud_NoConfigFallback()
    {
        // Config is present but SaaS must NOT fall back to it (confused-deputy).
        var sut = Build(TammaMode.SaaS, JiraConfig());

        var res = await sut.ResolveAsync(Tenant);

        res.Should().BeNull("SaaS with no tenant BYOK bundle fails loud, never the shared config");
    }

    [Test]
    public async Task SingleUser_ConfigPresent_ResolvesSystemTier()
    {
        var sut = Build(TammaMode.SingleUser, JiraConfig());

        var res = await sut.ResolveAsync(tenantId: null);

        res.Should().NotBeNull();
        res!.Source.Should().Be(IntegrationCredentialSource.System);
        res.Credential.BaseUrl.Should().Be("https://sys.example.com");
    }

    [Test]
    public async Task SingleUser_NoConfig_FailsLoud()
    {
        var sut = Build(TammaMode.SingleUser, Empty());

        var res = await sut.ResolveAsync(tenantId: null);

        res.Should().BeNull();
    }

    [Test]
    public async Task Saas_MalformedBundle_TreatedAsAbsent_FailsLoud()
    {
        _cabinet.Set(Tenant, IntegrationCabinetNames.JiraConfig, "{ not valid json ");
        var sut = Build(TammaMode.SaaS);

        var res = await sut.ResolveAsync(Tenant);

        res.Should().BeNull();
    }

    [Test]
    public async Task Invalidate_ReReadsCabinet()
    {
        _cabinet.Set(Tenant, IntegrationCabinetNames.JiraConfig, Bundle(token: "fake-old-token"));
        var sut = Build(TammaMode.SaaS);

        (await sut.ResolveAsync(Tenant))!.Credential.ApiToken.Should().Be("fake-old-token");

        _cabinet.Set(Tenant, IntegrationCabinetNames.JiraConfig, Bundle(token: "fake-new-token"));
        // Cached — still old until invalidated.
        (await sut.ResolveAsync(Tenant))!.Credential.ApiToken.Should().Be("fake-old-token");

        sut.Invalidate(Tenant);
        (await sut.ResolveAsync(Tenant))!.Credential.ApiToken.Should().Be("fake-new-token");
    }

    /// <summary>Minimal mutable fake for the tenant-scoped cabinet read seam.</summary>
    private sealed class FakeCabinetReader : ITenantProviderKeyReader
    {
        private readonly Dictionary<(Guid, string), string> _rows = new();

        public void Set(Guid tenantId, string name, string plaintext) => _rows[(tenantId, name)] = plaintext;

        public Task<TenantProviderKey?> TryReadAsync(Guid tenantId, string cabinetName, CancellationToken ct = default)
            => Task.FromResult(_rows.TryGetValue((tenantId, cabinetName), out var v)
                ? new TenantProviderKey(v, 1)
                : null);
    }
}
