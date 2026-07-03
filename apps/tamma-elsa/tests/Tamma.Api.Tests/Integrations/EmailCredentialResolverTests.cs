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
/// Email BYOK resolver — tenant→system→fail-loud. Pins: tenant Resend/SMTP bundle
/// wins (SaaS); single-user Email:* config is the system tier; SaaS with no bundle
/// fails loud (NO config fallback); an incomplete bundle (resend w/o key) is absent.
/// </summary>
[TestFixture]
public class EmailCredentialResolverTests
{
    private const string FakeResendKey = "fake-resend-key-value";
    private static readonly Guid Tenant = Guid.NewGuid();

    private FakeCabinetReader _cabinet = null!;

    [SetUp]
    public void SetUp() => _cabinet = new FakeCabinetReader();

    private EmailCredentialResolver Build(TammaMode mode, IConfiguration? config = null)
    {
        var modeProvider = new Mock<ITammaModeProvider>();
        modeProvider.SetupGet(m => m.Mode).Returns(mode);
        return new EmailCredentialResolver(
            _cabinet, config ?? Empty(), modeProvider.Object,
            NullLogger<EmailCredentialResolver>.Instance);
    }

    private static IConfiguration Empty() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration ResendConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "resend",
            ["Email:From"] = "noreply@sys.example.com",
            ["Email:Resend:ApiKey"] = "fake-system-resend-key",
        }).Build();

    private static IConfiguration SmtpFromOnlyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:From"] = "noreply@sys.example.com",
        }).Build();

    [Test]
    public async Task Saas_ResendBundlePresent_ResolvesTenantTier()
    {
        var bundle = EmailCredentialCodec.Serialize(new EmailCredential(
            EmailCredential.TransportResend, "team@tenant.example.com", ResendApiKey: FakeResendKey));
        _cabinet.Set(Tenant, IntegrationCabinetNames.EmailConfig, bundle);
        var sut = Build(TammaMode.SaaS);

        var res = await sut.ResolveAsync(Tenant);

        res.Should().NotBeNull();
        res!.Source.Should().Be(IntegrationCredentialSource.Tenant);
        res.Credential.Transport.Should().Be(EmailCredential.TransportResend);
        res.Credential.From.Should().Be("team@tenant.example.com");
        res.Credential.ResendApiKey.Should().Be(FakeResendKey);
    }

    [Test]
    public async Task Saas_NoBundle_FailsLoud_NoConfigFallback()
    {
        var sut = Build(TammaMode.SaaS, ResendConfig());

        var res = await sut.ResolveAsync(Tenant);

        res.Should().BeNull("SaaS with no tenant email bundle fails loud, never the shared config");
    }

    [Test]
    public async Task Saas_IncompleteBundle_ResendWithoutKey_TreatedAsAbsent()
    {
        // Serialize a resend bundle WITHOUT the api key → codec rejects it as incomplete.
        var bundle = EmailCredentialCodec.Serialize(new EmailCredential(
            EmailCredential.TransportResend, "team@tenant.example.com", ResendApiKey: null));
        _cabinet.Set(Tenant, IntegrationCabinetNames.EmailConfig, bundle);
        var sut = Build(TammaMode.SaaS);

        (await sut.ResolveAsync(Tenant)).Should().BeNull();
    }

    [Test]
    public async Task SingleUser_ResendConfig_ResolvesSystemTier()
    {
        var sut = Build(TammaMode.SingleUser, ResendConfig());

        var res = await sut.ResolveAsync(tenantId: null);

        res.Should().NotBeNull();
        res!.Source.Should().Be(IntegrationCredentialSource.System);
        res.Credential.Transport.Should().Be(EmailCredential.TransportResend);
        res.Credential.From.Should().Be("noreply@sys.example.com");
    }

    [Test]
    public async Task SingleUser_SmtpFromOnlyConfig_ResolvesSystemTier()
    {
        // SMTP tier: from-present is sufficient (the outbox host is supplied at the
        // sender). Default provider = smtp when Email:Provider is unset.
        var sut = Build(TammaMode.SingleUser, SmtpFromOnlyConfig());

        var res = await sut.ResolveAsync(tenantId: null);

        res.Should().NotBeNull();
        res!.Source.Should().Be(IntegrationCredentialSource.System);
        res.Credential.Transport.Should().Be(EmailCredential.TransportSmtp);
    }

    [Test]
    public async Task SingleUser_NoFrom_FailsLoud()
    {
        var sut = Build(TammaMode.SingleUser, Empty());

        (await sut.ResolveAsync(tenantId: null)).Should().BeNull();
    }

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
