using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Tests for <see cref="PromptFileLoader"/>, the embedded-resource loader behind
/// the <see cref="SystemPrompts"/> facade. The prompt registry lives in
/// <c>Prompts/{role}/{action}.md</c> repo files (front matter + verbatim body);
/// these tests pin the loader's parsing, its taxonomy-drift fail-loud behavior
/// in both directions, and the front-matter round-trip.
/// </summary>
[TestFixture]
public class PromptFileLoaderTests
{
    /// <summary>The exact count of jagged (role, action) cells from SPEC §4.</summary>
    private static int ExpectedCellCount =>
        RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count);

    private static IReadOnlyList<PromptFileLoader.PromptFile> EmbeddedFiles =>
        PromptFileLoader.ReadEmbeddedFiles();

    // ------------------------------------------------------------------
    // Happy path — embedded resources
    // ------------------------------------------------------------------

    [Test]
    public void Load_CellCount_MatchesTaxonomy()
    {
        var (systemPrompts, templates) = PromptFileLoader.Load();

        templates.Should().HaveCount(ExpectedCellCount,
            "there must be exactly one embedded prompt file per RolePhaseMap taxonomy cell");
        systemPrompts.Should().HaveCount(RolePhaseMap.ValidRoles.Count,
            "every role must ship a Prompts/{role}/_system.md identity preamble");
    }

    [Test]
    public void Load_EmbeddedResources_ExistForEveryTaxonomyCell()
    {
        var paths = EmbeddedFiles.Select(f => f.Path).ToHashSet();

        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            var roleWire = role.ToWire();
            paths.Should().Contain($"Prompts/{roleWire}/_system.md");
            foreach (var action in actions)
            {
                paths.Should().Contain($"Prompts/{roleWire}/{action.ToWire()}.md");
            }
        }
    }

    // ------------------------------------------------------------------
    // Drift — both directions fail loud
    // ------------------------------------------------------------------

    [Test]
    public void Build_MissingTaxonomyCell_ThrowsNoBodyFamily_NamingTheCell()
    {
        // Remove one real cell file (developer/refactor) from the embedded set.
        var files = EmbeddedFiles
            .Where(f => f.Path != "Prompts/developer/refactor.md")
            .ToList();

        var act = () => PromptFileLoader.Build(files);

        act.Should().Throw<TammaError>()
            .Which.Should().Match<TammaError>(e =>
                e.Code == "PROMPT.SEED.NO_BODY_FAMILY" &&
                e.Message.Contains("developer/refactor") &&
                (string?)e.Context["role"] == "developer" &&
                (string?)e.Context["action"] == "refactor");
    }

    [Test]
    public void Build_FileOutsideTaxonomy_ThrowsUnknownCell()
    {
        // 'deploy' is devops-only — a developer/deploy.md file is drift.
        var files = EmbeddedFiles.Append(new PromptFileLoader.PromptFile(
            "Prompts/developer/deploy.md",
            "---\nvariables: role\nenableTools: false\nmaxTokens: 1024\nversion: 1\n---\nbody"));

        var act = () => PromptFileLoader.Build(files);

        act.Should().Throw<TammaError>()
            .Which.Should().Match<TammaError>(e =>
                e.Code == "PROMPT.SEED.UNKNOWN_CELL" &&
                e.Message.Contains("Prompts/developer/deploy.md"));
    }

    [Test]
    public void Build_UnknownRoleDirectory_ThrowsUnknownCell()
    {
        var files = EmbeddedFiles.Append(new PromptFileLoader.PromptFile(
            "Prompts/wizard/_system.md",
            "---\nversion: 1\n---\nYou are a wizard."));

        var act = () => PromptFileLoader.Build(files);

        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("PROMPT.SEED.UNKNOWN_CELL");
    }

    [Test]
    public void Build_MissingSystemPromptFile_Throws()
    {
        var files = EmbeddedFiles
            .Where(f => f.Path != "Prompts/tech_writer/_system.md")
            .ToList();

        var act = () => PromptFileLoader.Build(files);

        act.Should().Throw<TammaError>()
            .Which.Should().Match<TammaError>(e =>
                e.Code == "PROMPT.SEED.MISSING_SYSTEM_PROMPT" &&
                e.Message.Contains("tech_writer"));
    }

    // ------------------------------------------------------------------
    // Malformed front matter — throws naming the file path
    // ------------------------------------------------------------------

    [TestCase("no front matter at all", TestName = "MissingOpeningDelimiter")]
    [TestCase("---\nvariables: role\nenableTools: false\nmaxTokens: 1024\nversion: 1\nbody without closing", TestName = "MissingClosingDelimiter")]
    [TestCase("---\nnot-a-pair\n---\nbody", TestName = "LineWithoutColon")]
    [TestCase("---\nvariables: role\nenableTools: maybe\nmaxTokens: 1024\nversion: 1\n---\nbody", TestName = "NonBooleanEnableTools")]
    [TestCase("---\nvariables: role\nenableTools: false\nmaxTokens: lots\nversion: 1\n---\nbody", TestName = "NonIntegerMaxTokens")]
    [TestCase("---\nvariables: role\nenableTools: false\nmaxTokens: 1024\n---\nbody", TestName = "MissingRequiredKey")]
    [TestCase("---\nvariables: role\nenableTools: false\nmaxTokens: 1024\nversion: 1\nbogus: x\n---\nbody", TestName = "UnknownKey")]
    public void Build_MalformedFrontMatter_ThrowsWithFilePath(string content)
    {
        const string path = "Prompts/developer/refactor.md";
        var files = EmbeddedFiles
            .Where(f => f.Path != path)
            .Append(new PromptFileLoader.PromptFile(path, content));

        var act = () => PromptFileLoader.Build(files);

        act.Should().Throw<TammaError>()
            .Which.Should().Match<TammaError>(e =>
                e.Code == "PROMPT.SEED.MALFORMED_FILE" &&
                e.Message.Contains(path));
    }

    // ------------------------------------------------------------------
    // Front-matter round-trip
    // ------------------------------------------------------------------

    [Test]
    public void Build_FrontMatter_RoundTripsVariablesFlagsAndBody()
    {
        const string path = "Prompts/developer/refactor.md";
        const string body = "Line one {{alpha}}\n\nLine three {{beta}}";
        var files = EmbeddedFiles
            .Where(f => f.Path != path)
            .Append(new PromptFileLoader.PromptFile(
                path,
                "---\nvariables: alpha, beta\nenableTools: true\nmaxTokens: 2048\nversion: 3\n---\n" + body));

        var (_, templates) = PromptFileLoader.Build(files);
        var cell = templates.Single(t => t.Role == "developer" && t.Action == "refactor");

        cell.Variables.Should().Equal("alpha", "beta");
        cell.EnableTools.Should().BeTrue();
        cell.MaxTokens.Should().Be(2048);
        cell.Version.Should().Be(3);
        cell.Template.Should().Be(body, "the body must be preserved verbatim after the closing delimiter");
        cell.SystemPrompt.Should().Be(SystemPrompts.RoleSystemPrompts["developer"]);
    }

    [Test]
    public void Load_KnownCells_CarryParsedFrontMatter()
    {
        // Spot-pin real files' front matter through the public facade.
        var contextScan = SystemPrompts.GetRoleAction("developer", "context-scan");
        contextScan.Should().NotBeNull();
        contextScan!.Variables.Should().Equal("role", "workItemType", "workItemJson", "previousFindings");
        contextScan.EnableTools.Should().BeTrue();
        contextScan.MaxTokens.Should().Be(4096);
        contextScan.Version.Should().Be(1);

        var implement = SystemPrompts.GetRoleAction("developer", "implement-feature");
        implement.Should().NotBeNull();
        implement!.MaxTokens.Should().Be(16384);
        implement.EnableTools.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Spot check — a known cell body carries its output contract
    // ------------------------------------------------------------------

    [Test]
    public void Load_ResearchCellBody_ContainsParserContractKeys()
    {
        var (_, templates) = PromptFileLoader.Load();
        var research = templates.Single(t => t.Role == "product_owner" && t.Action == "research");

        var body = research.Template;
        body.Should().Contain("\"summary\"");
        body.Should().Contain("\"findings\"");
        body.Should().Contain("\"relevance\"");
        body.Should().Contain("\"confidence\"");
        body.Should().Contain("\"citations\"");
        body.Should().Contain("\"overallConfidence\"");
    }
}
