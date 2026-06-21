using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Credentials;

namespace Tamma.Activities.Tests.LlmCall.Credentials;

/// <summary>
/// Story 32-3 review fix — Program-level wiring test for the standalone Elsa
/// workflow host (<c>Tamma.ElsaServer</c>), which executes
/// <see cref="CallLlmInlineActivity"/>.
///
/// <para><b>The defect this guards.</b> The credential resolver was registered
/// ONLY in <c>Tamma.Api/Program.cs</c>, but the activity runs in the
/// <c>Tamma.ElsaServer</c> process (which never references <c>Tamma.Api</c>). So
/// at runtime the activity bound a <c>null</c> resolver and sent an EMPTY
/// ApiKey. These tests assert that the SAME registration <c>Program.cs</c> now
/// calls — <see cref="EngineProviderCredentialServiceCollectionExtensions
/// .AddEngineProviderCredentialResolution"/> — produces a NON-null
/// <see cref="IProviderCredentialResolver"/> in the engine host's DI container,
/// and that the activity, when handed that resolver, resolves the platform key
/// (never an empty key).</para>
/// </summary>
[TestFixture]
public class EngineProviderCredentialWiringTests
{
    /// <summary>
    /// Builds the engine host's DI container the way <c>Program.cs</c> does:
    /// register <see cref="IConfiguration"/> + call
    /// <c>AddEngineProviderCredentialResolution()</c>.
    /// </summary>
    private static ServiceProvider BuildEngineContainer(
        IDictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        // The exact call Tamma.ElsaServer/Program.cs makes.
        services.AddEngineProviderCredentialResolution();

        return services.BuildServiceProvider();
    }

    [Test]
    public void EngineHostContainer_ResolvesNonNullCredentialResolver()
    {
        // THIS is the test that would have caught the defect: the engine host's
        // container, wired by AddEngineProviderCredentialResolution(), MUST be
        // able to resolve a non-null IProviderCredentialResolver.
        using var provider = BuildEngineContainer();

        var resolver = provider.GetService<IProviderCredentialResolver>();

        resolver.Should().NotBeNull(
            "the activity executes in the Elsa engine host and binds whatever " +
            "IProviderCredentialResolver that host's container provides — a null " +
            "resolver means an empty ApiKey is sent (the 32-3 regression).");
        resolver.Should().BeOfType<ConfigPlatformProviderCredentialResolver>();
    }

    [Test]
    public async Task EngineHostResolver_ResolvesPlatformKeyFromLlmProvidersConfig()
    {
        // AC12 — a deployment that supplies the platform key via the existing
        // LlmProviders:<provider>:ApiKey appsettings slot still authenticates.
        using var provider = BuildEngineContainer(new Dictionary<string, string?>
        {
            ["LlmProviders:anthropic:ApiKey"] = "PLATFORM-ANTHROPIC-KEY",
        });
        var resolver = provider.GetRequiredService<IProviderCredentialResolver>();

        var cred = await resolver.ResolveAsync(
            tenantId: null, "anthropic", CancellationToken.None);

        cred.ApiKey.Should().Be("PLATFORM-ANTHROPIC-KEY", "the platform key flows through (AC12)");
        cred.Source.Should().Be(CredentialSource.Platform);
    }

    [Test]
    public async Task EngineHostResolver_ResolvesPlatformKeyFromLegacySlot()
    {
        // AC12 — the pre-32-3 per-provider slot (Anthropic:ApiKey) that the
        // activity used to read directly must still produce a key.
        using var provider = BuildEngineContainer(new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = "LEGACY-ANTHROPIC-KEY",
        });
        var resolver = provider.GetRequiredService<IProviderCredentialResolver>();

        var cred = await resolver.ResolveAsync(
            tenantId: null, "anthropic", CancellationToken.None);

        cred.ApiKey.Should().Be("LEGACY-ANTHROPIC-KEY");
        cred.Source.Should().Be(CredentialSource.Platform);
    }

    [Test]
    public void EngineHostResolver_NoPlatformKey_FailsClosed_NeverEmptyKey()
    {
        // AC6 — when no platform key is configured the resolver throws the
        // fail-closed TammaError; it must NEVER hand back an empty key.
        using var provider = BuildEngineContainer();
        var resolver = provider.GetRequiredService<IProviderCredentialResolver>();

        var act = async () => await resolver.ResolveAsync(
            tenantId: null, "anthropic", CancellationToken.None);

        act.Should().ThrowAsync<Tamma.Core.TammaError>()
            .Where(e => e.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE");
    }

    [Test]
    public async Task ActivityBoundToEngineResolver_GetsPlatformKey_NotEmpty()
    {
        // End-to-end: the activity, constructed with the engine host's resolver,
        // populates a NON-empty ApiKey. This proves the fix at the seam the
        // defect lived at (LoadProviderConfigWithKeyAsync's null-resolver branch
        // sent an empty key in production).
        using var provider = BuildEngineContainer(new Dictionary<string, string?>
        {
            ["LlmProviders:openai:ApiKey"] = "PLATFORM-OPENAI-KEY",
        });
        var resolver = provider.GetRequiredService<IProviderCredentialResolver>();

        var activity = new CallLlmInlineActivity(
            logger: null, httpClientFactory: null, configuration: null, sanitizer: null,
            toolRegistry: null, toolCallValidator: null, contextCompactor: null,
            eventEmitter: null, parallelExecutor: null, credentialResolver: resolver);

        var (config, source) = await activity.LoadProviderConfigWithKeyAsync(
            "openai", tenantId: null, CancellationToken.None);

        config.ApiKey.Should().Be("PLATFORM-OPENAI-KEY",
            "with the engine resolver wired the activity no longer sends an empty key");
        source.Should().Be("platform");
    }
}
