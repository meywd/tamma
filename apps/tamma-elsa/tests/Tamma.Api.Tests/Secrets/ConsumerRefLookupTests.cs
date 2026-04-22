using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="ConsumerRefLookup"/>. The lookup is the seam
/// the admin UIs use to render "Used by: Tamma API" instead of raw
/// system / identifier strings, so the rendered shape — display name,
/// raw identifier, deep-link URL — has to be stable across every
/// canonical system key.
/// </summary>
[TestFixture]
public class ConsumerRefLookupTests
{
    private static readonly string[] AllKnownSystems = new[]
    {
        ConsumerRefLookup.Systems.Postgres,
        ConsumerRefLookup.Systems.TammaApi,
        ConsumerRefLookup.Systems.CranlApp,
        ConsumerRefLookup.Systems.GitHubWebhook,
        ConsumerRefLookup.Systems.GitLabWebhook,
        ConsumerRefLookup.Systems.ElsaWorkflow,
        ConsumerRefLookup.Systems.SmtpRelay,
        ConsumerRefLookup.Systems.OpenAiApi,
        ConsumerRefLookup.Systems.AnthropicApi,
    };

    [Test]
    public void KnownSystems_ContainsEveryConstantOnSystemsClass()
    {
        // The constants and the dictionary live in the same file so a
        // typo or missed registration is caught at test time rather
        // than producing a silent "unknown_system" pill in production.
        ConsumerRefLookup.KnownSystems
            .Should().BeEquivalentTo(AllKnownSystems);
    }

    [TestCaseSource(nameof(AllKnownSystems))]
    public void TryGetDefinition_ReturnsDefinitionForEveryKnownSystem(string systemKey)
    {
        var def = ConsumerRefLookup.TryGetDefinition(systemKey);
        def.Should().NotBeNull();
        def!.SystemKey.Should().Be(systemKey);
        def.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryGetDefinition_IsCaseInsensitive()
    {
        // The dictionary is built with OrdinalIgnoreCase so callers
        // passing "POSTGRES" / "Postgres" / "postgres" all resolve.
        ConsumerRefLookup.TryGetDefinition("POSTGRES").Should().NotBeNull();
        ConsumerRefLookup.TryGetDefinition("Postgres").Should().NotBeNull();
        ConsumerRefLookup.TryGetDefinition("postgres").Should().NotBeNull();
    }

    [Test]
    public void TryGetDefinition_ReturnsNullForUnknownSystem()
    {
        ConsumerRefLookup.TryGetDefinition("not-a-real-system")
            .Should().BeNull();
    }

    [Test]
    public void Render_KnownSystem_NoLinkTemplate_ProducesNullUrl()
    {
        var rendered = ConsumerRefLookup.Render(
            new ConsumerRef(
                ConsumerRefLookup.Systems.Postgres,
                "role=tamma_app"));

        rendered.DisplayName.Should().Be("Postgres");
        rendered.Identifier.Should().Be("role=tamma_app");
        rendered.Url.Should().BeNull();
    }

    [Test]
    public void Render_KnownSystem_WithLinkTemplate_SubstitutesIdentifier()
    {
        var rendered = ConsumerRefLookup.Render(
            new ConsumerRef(
                ConsumerRefLookup.Systems.CranlApp,
                "app_xyz"));

        rendered.DisplayName.Should().Be("Cranl App");
        rendered.Identifier.Should().Be("app_xyz");
        rendered.Url.Should().Be("https://cranl.io/apps/app_xyz");
    }

    [Test]
    public void Render_GitHubWebhook_BuildsRepoSettingsLink()
    {
        var rendered = ConsumerRefLookup.Render(
            new ConsumerRef(
                ConsumerRefLookup.Systems.GitHubWebhook,
                "acme/api"));

        rendered.Url.Should().Be("https://github.com/acme/api/settings/hooks");
    }

    [Test]
    public void Render_UnknownSystem_FallsBackToRawSystemKey()
    {
        var rendered = ConsumerRefLookup.Render(
            new ConsumerRef("custom-thing", "id-1"));

        rendered.DisplayName.Should().Be("custom-thing");
        rendered.Identifier.Should().Be("id-1");
        rendered.Url.Should().BeNull();
    }

    [Test]
    public void Render_ThrowsOnNull()
    {
        Action act = () => ConsumerRefLookup.Render(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void RenderAll_PreservesOrderAndMapsEachConsumer()
    {
        var consumers = new[]
        {
            new ConsumerRef(ConsumerRefLookup.Systems.Postgres, "role=tamma_app"),
            new ConsumerRef(ConsumerRefLookup.Systems.CranlApp, "app_one"),
            new ConsumerRef("unknown-thing", "x"),
        };

        var rendered = ConsumerRefLookup.RenderAll(consumers);

        rendered.Should().HaveCount(3);
        rendered[0].DisplayName.Should().Be("Postgres");
        rendered[1].DisplayName.Should().Be("Cranl App");
        rendered[1].Url.Should().Be("https://cranl.io/apps/app_one");
        rendered[2].DisplayName.Should().Be("unknown-thing");
    }

    [Test]
    public void RenderAll_ThrowsOnNullCollection()
    {
        Action act = () => ConsumerRefLookup.RenderAll(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ConsumerRef_WithIdentifier_ReturnsCopyWithReplacedIdentifier()
    {
        var original = new ConsumerRef("postgres", "role=app");
        var modified = original.WithIdentifier("role=other");

        modified.System.Should().Be("postgres");
        modified.Identifier.Should().Be("role=other");
        original.Identifier.Should().Be("role=app",
            because: "records are immutable; with-expressions never mutate the source");
    }
}
