using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Tests for variable substitution semantics inside <see cref="PromptStoreService"/>.
/// Single-pass, no recursive expansion, missing variables surfaced, length bounds enforced.
/// </summary>
[TestFixture]
public class PromptRenderTests
{
    [Test]
    public void Render_SubstitutesAllProvidedVariables()
    {
        var result = PromptStoreService.Render(
            "Hello {{name}}, welcome to {{project}}.",
            new Dictionary<string, string>
            {
                ["name"] = "Alice",
                ["project"] = "Tamma",
            });

        result.Rendered.Should().Be("Hello Alice, welcome to Tamma.");
        result.Unresolved.Should().BeEmpty();
    }

    [Test]
    public void Render_LeavesUnknownVariables_InPlace_AndTracksThem()
    {
        var result = PromptStoreService.Render(
            "Hello {{name}}, score: {{score}}.",
            new Dictionary<string, string>
            {
                ["name"] = "Alice",
            });

        result.Rendered.Should().Be("Hello Alice, score: {{score}}.");
        result.Unresolved.Should().BeEquivalentTo(new[] { "score" });
    }

    [Test]
    public void Render_EmptyTemplate_ReturnsEmptyString()
    {
        var result = PromptStoreService.Render("", new Dictionary<string, string>());

        result.Rendered.Should().BeEmpty();
        result.Unresolved.Should().BeEmpty();
    }

    [Test]
    public void Render_NoVariables_ReturnsTemplateUnchanged()
    {
        var result = PromptStoreService.Render("Static content without vars.", new Dictionary<string, string>());

        result.Rendered.Should().Be("Static content without vars.");
        result.Unresolved.Should().BeEmpty();
    }

    [Test]
    public void Render_SinglePassOnly_DoesNotExpandNestedPlaceholders()
    {
        // If substitution were recursive, passing {{inner}} for outer would cause
        // further expansion. Single-pass must leave the raw substituted value.
        var result = PromptStoreService.Render(
            "{{outer}}",
            new Dictionary<string, string>
            {
                ["outer"] = "{{inner}}",
                ["inner"] = "BAD",
            });

        result.Rendered.Should().Be("{{inner}}");
    }

    [Test]
    public void Render_TruncatesValues_LargerThan_MaxVariableLength()
    {
        var huge = new string('x', PromptStoreService.MaxVariableValueLength + 1);
        var result = PromptStoreService.Render(
            "Value: {{big}}",
            new Dictionary<string, string>
            {
                ["big"] = huge,
            });

        // Over-size values are treated as unresolved to prevent template bloat
        result.Rendered.Should().Contain("{{big}}");
        result.Unresolved.Should().Contain("big");
    }

    [Test]
    public void Render_TracksDistinctUnresolvedVariables_NoDuplicates()
    {
        var result = PromptStoreService.Render(
            "{{missing}} then {{missing}} again",
            new Dictionary<string, string>());

        result.Unresolved.Should().BeEquivalentTo(new[] { "missing" });
    }

    [Test]
    public void Render_HandlesMultiLineTemplatesWithManyVariables()
    {
        var template = """
            Role: {{role}}
            Task: {{task}}
            Context: {{context}}
            """;

        var result = PromptStoreService.Render(
            template,
            new Dictionary<string, string>
            {
                ["role"] = "developer",
                ["task"] = "refactor",
                ["context"] = "monorepo",
            });

        result.Rendered.Should().Contain("developer")
                      .And.Contain("refactor")
                      .And.Contain("monorepo");
        result.Unresolved.Should().BeEmpty();
    }

    [Test]
    public void RenderFull_CombinesSystemAndUserTemplates()
    {
        var rendered = PromptStoreService.RenderFull(
            systemTemplate: "You are a {{role}}",
            userTemplate: "Do this for {{task}}",
            variables: new Dictionary<string, string>
            {
                ["role"] = "security engineer",
                ["task"] = "auth-review",
            });

        rendered.SystemPrompt.Should().Be("You are a security engineer");
        rendered.UserPrompt.Should().Be("Do this for auth-review");
        rendered.Unresolved.Should().BeEmpty();
    }

    [Test]
    public void RenderFull_MergesUnresolvedFromBothSides_WithoutDuplicates()
    {
        var rendered = PromptStoreService.RenderFull(
            systemTemplate: "{{a}} and {{missing}}",
            userTemplate: "{{b}} and {{missing}}",
            variables: new Dictionary<string, string>
            {
                ["a"] = "A",
                ["b"] = "B",
            });

        rendered.Unresolved.Should().BeEquivalentTo(new[] { "missing" });
    }
}
