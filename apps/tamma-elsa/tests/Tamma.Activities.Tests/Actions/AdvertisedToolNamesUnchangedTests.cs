using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-4 AC2 (D3) — the alias map is RESOLUTION-ONLY: the tool surface
/// advertised to the model is byte-identical before and after this story.
/// Pins (1) the exact <c>DefaultAgentConfig.Tools</c> arrays against hardcoded
/// pre-story literals, (2) that <c>ManagedAgent.ToResolvedTools</c> emits its
/// input names unchanged for a Claude-Code-named config, and (3) — the
/// grep-shaped assertion — that <c>ToolNameAliases</c> is not referenced from
/// any advertisement-path file. Making the Claude-Code names actually execute
/// is a privilege expansion filed OUTSIDE Epic 43; whoever ships it updates
/// these pins in that reviewed story, never as a drive-by.
/// </summary>
[TestFixture]
public class AdvertisedToolNamesUnchangedTests
{
    private static readonly string[] ClaudeCodeFullSet = { "Read", "Write", "Edit", "Bash", "Grep", "Glob" };
    private static readonly string[] ClaudeCodeReadOnlySet = { "Read", "Grep", "Glob" };

    // The eight seeded roles' advertised arrays, EXACTLY as they shipped before
    // Story 43-4 (DefaultAgentConfig.cs) — order-sensitive, byte-identical.
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedAdvertisedTools =
        new Dictionary<string, string[]>
        {
            ["developer"] = ClaudeCodeFullSet,
            ["tester"] = ClaudeCodeFullSet,
            ["security"] = ClaudeCodeReadOnlySet,
            ["devops"] = ClaudeCodeFullSet,
            ["architect"] = ClaudeCodeReadOnlySet,
            ["product_owner"] = Array.Empty<string>(),
            ["senior_developer"] = ClaudeCodeFullSet,
            ["tech_writer"] = ClaudeCodeReadOnlySet,
        };

    [Test]
    public void The_default_agent_config_arrays_are_byte_identical_to_the_pre_story_values()
    {
        // The eight roles that existed when 43-4 landed must be pinned exactly.
        // Roles other stories add later are not this pin's concern PROVIDED
        // their advertised names resolve — which the startup validator's
        // UNRESOLVABLE_TOOL_ALIAS check enforces for every role, present and
        // future.
        RolePhaseMap.ValidRoles.Should().Contain(ExpectedAdvertisedTools.Keys,
            "a pre-43-4 role was removed or renamed — update this pin deliberately");

        foreach (var (role, expected) in ExpectedAdvertisedTools)
        {
            DefaultAgentConfig.ForRole(role).Tools.Should().Equal(expected,
                $"role '{role}' must advertise exactly its pre-43-4 tool names, in order — "
                + "the alias map is resolution-only and must not leak into advertisement");
        }
    }

    [Test]
    public void ToResolvedTools_emits_the_input_names_unchanged()
    {
        // The advertisement path: for a Claude-Code-named config,
        // ManagedAgent.ToResolvedTools emits ResolvedTool.Name values equal to
        // its input names (the registry catalogue cannot match them, so they
        // pass through verbatim — and no alias map may change that). Exercised
        // through the real private method on an uninitialized instance (null
        // registry — the standalone-engine shape).
        var method = typeof(ManagedAgent).GetMethod(
            "ToResolvedTools",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull(
            "ManagedAgent.ToResolvedTools moved — re-point this advertisement pin at its successor");

        var agent = (ManagedAgent)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ManagedAgent));
        var request = new ManagedAgentRequest
        {
            Role = "developer",
            Prompt = "pin",
            CorrelationId = "pin",
            Tools = ClaudeCodeFullSet,
        };
        var resolved = new ResolvedAgentConfig
        {
            Role = "developer",
            Handle = "tamma-developer",
            Provider = "claude-code",
            Model = "pin",
            Tools = ClaudeCodeFullSet,
        };

        var result = (IReadOnlyList<Tamma.Activities.LlmCall.Models.ResolvedTool>?)
            method!.Invoke(agent, new object?[] { request, resolved });

        result.Should().NotBeNull();
        result!.Select(t => t.Name).Should().Equal(ClaudeCodeFullSet,
            "advertised names must round-trip byte-identical through ToResolvedTools");
    }

    [Test]
    public void ToolNameAliases_is_not_referenced_from_the_advertisement_path()
    {
        // D3's grep-shaped assertion: the map exists but MUST NOT be wired into
        // ManagedAgent, the registry, the advertised config, or ResolvedTool.
        var root = RepoRoot();
        var advertisementPathFiles = new[]
        {
            "src/Tamma.Api/Services/Agents/ManagedAgent.cs",
            "src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs",
            "src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs",
        };

        foreach (var relative in advertisementPathFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"advertisement-path file '{relative}' moved — update this scan");
            File.ReadAllText(path).Should().NotContain("ToolNameAliases",
                $"'{relative}' is on the advertisement path; wiring the alias map there is the "
                + "privilege expansion Story 43-4 explicitly does not ship");
        }

        // ResolvedTool's declaring file, wherever it lives.
        var resolvedToolFiles = Directory.EnumerateFiles(
                Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("class ResolvedTool"))
            .ToList();
        resolvedToolFiles.Should().NotBeEmpty("ResolvedTool's declaration should be findable");
        foreach (var file in resolvedToolFiles)
        {
            File.ReadAllText(file).Should().NotContain("ToolNameAliases");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tamma.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate apps/tamma-elsa (Tamma.sln) from " + AppContext.BaseDirectory);
    }
}
