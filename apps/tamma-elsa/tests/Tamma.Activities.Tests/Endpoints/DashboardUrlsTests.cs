using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// Story 45-7 — the one resolver for customer-facing link bases. The fallback
/// chain IS the compatibility contract: CustomerUrl → Url → default. A
/// deployment that sets only <c>Dashboard:Url</c> (every pre-split install)
/// must behave exactly as before the split.
/// </summary>
[TestFixture]
public sealed class DashboardUrlsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    [Test]
    public void CustomerUrl_wins_over_legacy_Url()
    {
        var config = Config(
            ("Dashboard:CustomerUrl", "https://dash.example.com"),
            ("Dashboard:Url", "https://admin.example.com"));

        DashboardUrls.CustomerBase(config).Should().Be("https://dash.example.com");
    }

    [Test]
    public void Legacy_Url_alone_is_used_verbatim_preserving_pre_split_behaviour()
    {
        var config = Config(("Dashboard:Url", "https://only.example.com"));

        DashboardUrls.CustomerBase(config).Should().Be("https://only.example.com");
    }

    [Test]
    public void Nothing_configured_falls_back_to_the_customer_host_default()
    {
        DashboardUrls.CustomerBase(Config()).Should().Be(DashboardUrls.DefaultCustomerUrl);
    }

    [Test]
    public void Empty_string_CustomerUrl_is_treated_as_unset()
    {
        var config = Config(
            ("Dashboard:CustomerUrl", ""),
            ("Dashboard:Url", "https://legacy.example.com"));

        DashboardUrls.CustomerBase(config).Should().Be("https://legacy.example.com");
    }

    [Test]
    public void Whitespace_CustomerUrl_is_treated_as_unset()
    {
        var config = Config(
            ("Dashboard:CustomerUrl", "   "),
            ("Dashboard:Url", "https://legacy.example.com"));

        DashboardUrls.CustomerBase(config).Should().Be("https://legacy.example.com");
    }

    [Test]
    public void Both_layers_empty_fall_through_to_the_default()
    {
        var config = Config(
            ("Dashboard:CustomerUrl", ""),
            ("Dashboard:Url", " "));

        DashboardUrls.CustomerBase(config).Should().Be(DashboardUrls.DefaultCustomerUrl);
    }

    [TestCase("https://dash.example.com/", "https://dash.example.com")]
    [TestCase("https://dash.example.com//", "https://dash.example.com")]
    public void Trailing_slashes_are_trimmed_so_links_never_double_slash(string configured, string expected)
    {
        var config = Config(("Dashboard:CustomerUrl", configured));

        DashboardUrls.CustomerBase(config).Should().Be(expected);
    }

    [Test]
    public void Legacy_Url_trailing_slash_is_trimmed_too()
    {
        var config = Config(("Dashboard:Url", "https://legacy.example.com/"));

        DashboardUrls.CustomerBase(config).Should().Be("https://legacy.example.com");
    }

    [Test]
    public void Default_constant_is_the_customer_host_with_no_trailing_slash()
    {
        DashboardUrls.DefaultCustomerUrl.Should().Be("https://dash.tamma.dev");
        DashboardUrls.DefaultCustomerUrl.Should().NotEndWith("/");
    }
}
