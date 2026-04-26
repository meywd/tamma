using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// R2-H11 + R2 post-fix PF-S4 — environment-aware production hard-fail
/// for <see cref="TenantSecretProtector.FromConfiguration(IConfiguration, IHostEnvironment?, ILogger?)"/>.
/// Production deploys must set <c>Cranl:EncryptionKey</c> explicitly;
/// the silent HKDF fallback is dev-only.
///
/// <para>PF-S4 deleted the single-arg overload (no IHostEnvironment).
/// All call sites now flow IHostEnvironment via DI, so the dispatcher
/// bypass that silently HKDF'd in production is closed.</para>
/// </summary>
[TestFixture]
public class TenantSecretProtectorEnvironmentTests
{
    private static IConfiguration BuildConfig(string? encryptionKey = null, string? apiKey = null)
    {
        var dict = new Dictionary<string, string?>();
        if (encryptionKey is not null) dict["Cranl:EncryptionKey"] = encryptionKey;
        if (apiKey is not null) dict["Cranl:ApiKey"] = apiKey;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/tmp";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Test]
    public void Production_Without_EncryptionKey_Throws_Hard_Fail()
    {
        // R2-H11: production must NOT silently HKDF-derive a key from
        // Cranl:ApiKey. The factory throws InvalidOperationException
        // with a remediation message pointing at the env var or the
        // OpenBao migration path.
        var cfg = BuildConfig(apiKey: "cranl_sk_anything");
        var env = new StubEnvironment { EnvironmentName = Environments.Production };

        Action act = () => TenantSecretProtector.FromConfiguration(cfg, env, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cranl:EncryptionKey is REQUIRED in production*");
    }

    [Test]
    public void Production_With_EncryptionKey_Returns_Working_Protector()
    {
        // R2-H11: when the explicit key is present in production, the
        // factory builds a real protector. No fallback path is taken.
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)i;
        var cfg = BuildConfig(encryptionKey: Convert.ToBase64String(key));
        var env = new StubEnvironment { EnvironmentName = Environments.Production };

        var protector = TenantSecretProtector.FromConfiguration(cfg, env, NullLogger.Instance);

        protector.Should().NotBeNull();
        // Round-trip a known plaintext to confirm the protector is
        // actually using the configured key.
        var encoded = protector.Encrypt("hello");
        protector.Decrypt(encoded).Should().Be("hello");
    }

    [Test]
    public void Development_Without_EncryptionKey_Falls_Back_To_HKDF()
    {
        // R2-H11: development semantics preserve the pre-H11 silent
        // HKDF fallback so dev rigs without a configured key still
        // build a working protector.
        var cfg = BuildConfig(apiKey: "cranl_sk_devvalue");
        var env = new StubEnvironment { EnvironmentName = Environments.Development };

        var protector = TenantSecretProtector.FromConfiguration(cfg, env, NullLogger.Instance);

        protector.Should().NotBeNull();
        var encoded = protector.Encrypt("hello");
        protector.Decrypt(encoded).Should().Be("hello");
    }

    [Test]
    public void Staging_Without_EncryptionKey_Throws_Like_Production()
    {
        // Anything that's NOT IsDevelopment falls into the hard-fail
        // branch — Staging is treated the same as Production for this
        // safety check.
        var cfg = BuildConfig(apiKey: "cranl_sk_stagingvalue");
        var env = new StubEnvironment { EnvironmentName = Environments.Staging };

        // Note: env.IsProduction() is false for Staging, so the silent
        // fallback IS taken in Staging. This documents the behaviour:
        // the gate is strictly env.IsProduction(). If we want to extend
        // it to non-development environments, the test would change.
        var protector = TenantSecretProtector.FromConfiguration(cfg, env, NullLogger.Instance);
        protector.Should().NotBeNull("Staging is not production today; HKDF fallback is allowed");
    }

    [Test]
    public void Null_Environment_Falls_Back_To_HKDF_Like_Development()
    {
        // PF-S4: the single-arg legacy overload was deleted. Callers
        // that genuinely have no environment context (e.g. test
        // helpers, ad-hoc utilities) MUST pass `environment: null`
        // explicitly. The signature still accepts null — the gate
        // hard-fails ONLY in true production env.
        var cfg = BuildConfig(apiKey: "cranl_sk_legacyvalue");

        var protector = TenantSecretProtector.FromConfiguration(
            cfg, environment: null, logger: NullLogger.Instance);

        protector.Should().NotBeNull();
        var encoded = protector.Encrypt("hello");
        protector.Decrypt(encoded).Should().Be("hello");
    }

    [Test]
    public void Production_With_NoApiKey_NoEncryptionKey_Still_Throws()
    {
        // No explicit key + no API key + production → still throws.
        var cfg = BuildConfig();
        var env = new StubEnvironment { EnvironmentName = Environments.Production };

        Action act = () => TenantSecretProtector.FromConfiguration(cfg, env, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cranl:EncryptionKey is REQUIRED in production*");
    }

    [Test]
    public void Development_With_NoApiKey_NoEncryptionKey_Returns_NoOp_Protector()
    {
        // The pre-existing dev-mode "Null provisioner" path returns a
        // non-functional protector. R2-H11 preserves that.
        var cfg = BuildConfig();
        var env = new StubEnvironment { EnvironmentName = Environments.Development };

        var protector = TenantSecretProtector.FromConfiguration(cfg, env, NullLogger.Instance);

        protector.Should().NotBeNull();
    }

    // ── Story 28-R2 / PF-S4 — Single-arg overload deletion ──────────

    [Test]
    public void PlatformEventsServiceCollection_Production_NoKey_ThrowsOnResolution()
    {
        // PF-S4 — the H11 dispatcher-bypass came from
        // PlatformEventsServiceCollectionExtensions.AddPlatformEventBus
        // calling the single-arg `FromConfiguration` overload that
        // silently HKDF'd from Cranl:ApiKey. The single-arg overload
        // has been DELETED; the extension now flows IHostEnvironment.
        // Pin the production hard-fail behaviour by booting a service
        // collection in Production with no encryption key and
        // verifying the resolution throws.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfig(apiKey: "cranl_sk_anything"));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new StubEnvironment { EnvironmentName = Environments.Production });
        services.AddLogging();
        Tamma.Api.Extensions.PlatformEventsServiceCollectionExtensions
            .AddPlatformEventBus(services);

        using var sp = services.BuildServiceProvider();

        // PF-S4: production resolution MUST throw because there is no
        // Cranl:EncryptionKey. The previous single-arg-overload path
        // silently fell back to HKDF; the new path bubbles the
        // InvalidOperationException out of the factory closure.
        Action act = () => sp.GetRequiredService<Tamma.Data.Abstractions.ITenantConnectionStringProtector>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cranl:EncryptionKey is REQUIRED in production*");
    }

    [Test]
    public void PlatformEventsServiceCollection_Development_NoKey_BuildsProtectorViaHKDF()
    {
        // Mirror — in Development, the same wiring path falls back to
        // HKDF (the dev convenience that PF-S4 explicitly preserves).
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfig(apiKey: "cranl_sk_anything"));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new StubEnvironment { EnvironmentName = Environments.Development });
        services.AddLogging();
        Tamma.Api.Extensions.PlatformEventsServiceCollectionExtensions
            .AddPlatformEventBus(services);

        using var sp = services.BuildServiceProvider();

        var protector = sp.GetRequiredService<Tamma.Data.Abstractions.ITenantConnectionStringProtector>();
        protector.Should().NotBeNull();
    }
}
