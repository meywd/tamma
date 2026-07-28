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

    // ── hostname re-layout — AdditionalOrigins (Dashboard:AdditionalOrigins CSV) ──

    [Test]
    public void AdditionalOrigins_unset_yields_nothing()
    {
        DashboardUrls.AdditionalOrigins(Config()).Should().BeEmpty(
            "a deployment with one hostname per app configures nothing extra");
    }

    [Test]
    public void AdditionalOrigins_empty_string_yields_nothing()
    {
        var config = Config(("Dashboard:AdditionalOrigins", ""));

        DashboardUrls.AdditionalOrigins(config).Should().BeEmpty();
    }

    [Test]
    public void AdditionalOrigins_whitespace_yields_nothing()
    {
        var config = Config(("Dashboard:AdditionalOrigins", "   "));

        DashboardUrls.AdditionalOrigins(config).Should().BeEmpty();
    }

    [Test]
    public void AdditionalOrigins_single_value_parses()
    {
        var config = Config(("Dashboard:AdditionalOrigins", "https://app.tamma.dev"));

        DashboardUrls.AdditionalOrigins(config).Should().Equal("https://app.tamma.dev");
    }

    [Test]
    public void AdditionalOrigins_csv_is_split_trimmed_and_blank_entries_dropped()
    {
        var config = Config(
            ("Dashboard:AdditionalOrigins",
             " https://app.example.com , https://alt.example.com ,, "));

        DashboardUrls.AdditionalOrigins(config).Should().Equal(
            "https://app.example.com", "https://alt.example.com");
    }

    [Test]
    public void AdditionalOrigins_feed_NormalizeOrigins_and_dedupe_with_primaries()
    {
        // The wiring contract: Program.cs concatenates the primaries with the
        // CSV entries and pushes everything through NormalizeOrigins, so a CSV
        // duplicate of a primary collapses to one policy entry.
        var config = Config(
            ("Dashboard:AdditionalOrigins", "https://app.tamma.dev, https://DASH.example.com/"));

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "https://dash.example.com" }
                .Concat(DashboardUrls.AdditionalOrigins(config)));

        origins.Should().Equal("https://dash.example.com", "https://app.tamma.dev");
    }

    // ── review F-CORS-2 — NormalizeOrigins (the CORS origin-list helper) ────

    [Test]
    public void NormalizeOrigins_bare_origins_pass_through_unchanged_no_warning()
    {
        var warnings = new List<string>();

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "https://dash.example.com", "http://localhost:3001" }, warnings.Add);

        origins.Should().Equal("https://dash.example.com", "http://localhost:3001");
        warnings.Should().BeEmpty();
    }

    [Test]
    public void NormalizeOrigins_path_carrying_url_reduces_to_the_authority_and_warns()
    {
        // Browsers send Origin as scheme://host[:port] — a configured value
        // with a path could never match WithOrigins' exact comparison.
        var warnings = new List<string>();

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "https://portal.example.com/dash" }, warnings.Add);

        origins.Should().Equal("https://portal.example.com");
        warnings.Should().ContainSingle()
            .Which.Should().Contain("https://portal.example.com/dash");
    }

    [Test]
    public void NormalizeOrigins_trailing_slash_is_authority_equivalent_no_warning()
    {
        // A lone trailing slash is trimmed BEFORE parsing (the pre-existing
        // TrimEnd behaviour) — not a misconfiguration worth warning about.
        var warnings = new List<string>();

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "https://dash.example.com/" }, warnings.Add);

        origins.Should().Equal("https://dash.example.com");
        warnings.Should().BeEmpty();
    }

    [Test]
    public void NormalizeOrigins_default_port_is_dropped_like_browsers_do()
    {
        var warnings = new List<string>();

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "https://dash.example.com:443/app" }, warnings.Add);

        origins.Should().ContainSingle(
                "browsers omit the default port from the Origin header")
            .Which.Should().Be("https://dash.example.com");
        warnings.Should().ContainSingle("the value changed, so the operator is told");
    }

    [Test]
    public void NormalizeOrigins_unparseable_value_kept_verbatim_with_warning()
    {
        var warnings = new List<string>();

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "not a url at all" }, warnings.Add);

        origins.Should().ContainSingle(
                "a mis-typed entry stays visible in the policy instead of vanishing")
            .Which.Should().Be("not a url at all");
        warnings.Should().ContainSingle().Which.Should().Contain("not a url at all");
    }

    [Test]
    public void NormalizeOrigins_dedupes_case_insensitively_and_drops_blanks()
    {
        var origins = DashboardUrls.NormalizeOrigins(new[]
        {
            "https://dash.example.com",
            "https://DASH.example.com/",
            "",
            "   ",
            null,
        });

        origins.Should().ContainSingle("single-user installs set one value for both apps");
    }

    [Test]
    public void NormalizeOrigins_query_and_fragment_also_reduce_to_the_authority()
    {
        var warnings = new List<string>();

        var origins = DashboardUrls.NormalizeOrigins(
            new[] { "https://dash.example.com/?utm=x" }, warnings.Add);

        origins.Should().Equal("https://dash.example.com");
        warnings.Should().ContainSingle();
    }
}
