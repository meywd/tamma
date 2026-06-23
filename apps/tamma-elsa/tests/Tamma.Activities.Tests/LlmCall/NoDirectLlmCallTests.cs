using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AI;
using Tamma.Activities.Debug;
using Tamma.Activities.LlmCall;
using Tamma.Activities.TDD;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 32-5 (AC9) — the grep-gate proving the engine holds no LLM provider key
/// and that no in-engine direct-LLM caller talks to a provider HTTP API.
///
/// <para>After the T6 cutover EVERY LLM call routes through
/// <c>POST /api/v1/llm/call</c> (via <see cref="TammaApiClient.CallLlmAsync"/>),
/// which lives in <c>Tamma.Api</c> and holds the request-scoped key. So under
/// <c>src/Tamma.Activities</c> there must be:</para>
/// <list type="bullet">
///   <item>ZERO <c>Anthropic:ApiKey</c> (or any <c>*:ApiKey</c> config) reads;</item>
///   <item>ZERO <c>POST /v1/messages</c> / <c>POST /v1/chat/completions</c>
///         provider calls EXCEPT inside the shared <see cref="InlineToolLoopRunner"/>
///         (which executes in the API process, where the key is resolved) and the
///         <see cref="TammaApiClient"/> wire client itself;</item>
///   <item>and the engine's <c>ElsaServer/Program.cs</c> registers NO provider
///         credential resolver (<c>AddEngineProviderCredentialResolution</c> /
///         <c>ConfigPlatformProviderCredentialResolver</c>).</item>
/// </list>
///
/// <para>This is a source-scan rather than a reflection/IL scan because the
/// violation is a literal string (the provider URL / the config key) — the
/// surest, most readable proof. The tests run from the worktree bin dir, so the
/// source tree is reachable by walking up from the test assembly location.</para>
/// </summary>
[TestFixture]
public class NoDirectLlmCallTests
{
    // Files that legitimately retain the provider HTTP call. The runner is the
    // single extracted tool-loop (AC4) and runs in the API process; the wire
    // client only POSTs to the call-LLM endpoint, not to a provider.
    private static readonly string[] AllowedProviderCallFiles =
    {
        Path.Combine("LlmCall", "InlineToolLoopRunner.cs"),
        Path.Combine("LlmCall", "TammaApiClient.cs"),
    };

    // The credential-resolver files are the 32-3 engine seam this story DELETES
    // from the engine's call path. They may physically remain in the
    // Tamma.Activities assembly (the type still exists for the API to reference),
    // but the ENGINE Program.cs must not wire them. The wiring assertion below
    // targets ElsaServer/Program.cs specifically.
    private static readonly string[] CredentialResolverDocFiles =
    {
        Path.Combine("LlmCall", "Credentials", "ConfigPlatformProviderCredentialResolver.cs"),
        Path.Combine("LlmCall", "Credentials", "EngineProviderCredentialServiceCollectionExtensions.cs"),
    };

    private static readonly Regex ProviderMessagesCall =
        new(@"PostAs(?:Json)?Async\(\s*\$?""[^""]*\/v1\/messages", RegexOptions.Compiled);

    private static readonly Regex ProviderChatCompletionsCall =
        new(@"PostAs(?:Json)?Async\(\s*\$?""[^""]*\/v1\/chat\/completions", RegexOptions.Compiled);

    private static readonly Regex ApiKeyConfigRead =
        new(@"_?[Cc]onfiguration(?:\?)?\[\s*""[A-Za-z]+:ApiKey""", RegexOptions.Compiled);

    [Test]
    public void TammaActivities_HasNoDirectProviderMessagesCall_OutsideRunnerAndWireClient()
    {
        var offenders = ActivitiesSourceFiles()
            .Where(f => !IsAllowedProviderCallFile(f.Relative))
            .Where(f => ProviderMessagesCall.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may POST to /v1/messages — every LLM call routes through "
            + "TammaApiClient.CallLlmAsync → POST /api/v1/llm/call. Offending files: "
            + string.Join(", ", offenders));
    }

    [Test]
    public void TammaActivities_HasNoDirectProviderChatCompletionsCall_OutsideRunnerAndWireClient()
    {
        var offenders = ActivitiesSourceFiles()
            .Where(f => !IsAllowedProviderCallFile(f.Relative))
            .Where(f => ProviderChatCompletionsCall.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may POST to /v1/chat/completions. Offending files: "
            + string.Join(", ", offenders));
    }

    [Test]
    public void TammaActivities_HasNoApiKeyConfigRead_OutsideCredentialResolver()
    {
        // The only place a *:ApiKey config slot may be read is the 32-3
        // ConfigPlatformProviderCredentialResolver (the engine no longer WIRES
        // it — asserted separately — but the type still references the key as
        // its documented platform-key source for the API's use).
        var offenders = ActivitiesSourceFiles()
            .Where(f => !IsCredentialResolverDoc(f.Relative))
            .Where(f => ApiKeyConfigRead.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may read a provider *:ApiKey config slot — credential "
            + "resolution lives in Tamma.Api. Offending files: " + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------------
    // Per-caller reflection proof: the cut-over types no longer expose a direct
    // keyed-LLM method (CallLlm / CallClaudeApi / CallAnthropicApi / the
    // non-mediated CallEngineCallback). They route through MediatedLlmText →
    // TammaApiClient.CallLlmAsync instead. (AC9.)
    // ---------------------------------------------------------------------

    private static readonly Type[] CutOverActivityTypes =
    {
        typeof(WriteTestsActivity),
        typeof(WriteImplementationActivity),
        typeof(AnalyzeCodeActivity),
        typeof(ApplyRefactoringActivity),
        typeof(Tamma.Activities.ADL.ApplyReviewFixesActivity),
        typeof(AIDiagnosisActivity),
        typeof(ClaudeAnalysisActivity),
    };

    private static readonly string[] ForbiddenDirectCallMethodNames =
    {
        "CallLlm", "CallClaudeApi", "CallAnthropicApi", "CallOpenAiCompatibleApi", "CallEngineCallback",
    };

    [Test]
    [TestCaseSource(nameof(CutOverActivityTypes))]
    public void CutOverActivity_HasNoDirectLlmCallMethod(Type activityType)
    {
        var offenders = activityType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => ForbiddenDirectCallMethodNames.Contains(m.Name))
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            $"{activityType.Name} must route its LLM call through the mediated call-LLM endpoint "
            + "(MediatedLlmText → TammaApiClient.CallLlmAsync), not a direct keyed provider call. "
            + "Offending methods: " + string.Join(", ", offenders));
    }

    [Test]
    public void MediatedLlmText_IsTheSingleSharedMediationSeam()
    {
        // The shared helper exists in the LlmCall namespace and is the one place
        // the cut-over callers go through. (Type presence + the CompleteAsync seam.)
        var type = typeof(CallLlmInlineActivity).Assembly
            .GetType("Tamma.Activities.LlmCall.MediatedLlmText");
        type.Should().NotBeNull("the cut-over callers share a single mediation helper");
        type!.GetMethod("CompleteAsync", BindingFlags.Public | BindingFlags.Static)
            .Should().NotBeNull("MediatedLlmText.CompleteAsync is the shared text-completion seam");
    }

    [Test]
    public void ElsaServerProgram_RegistersNoProviderCredentialResolver()
    {
        var program = ReadFile(Path.Combine(ElsaServerSrcDir(), "Program.cs"));

        program.Should().NotContain("AddEngineProviderCredentialResolution",
            "the engine must hold NO LLM provider key — the 32-3 engine resolver wiring is deleted (AC9)");
        program.Should().NotContain("ConfigPlatformProviderCredentialResolver",
            "the engine must not construct/register the config-backed platform-key resolver (AC9)");
    }

    // ---------------------------------------------------------------------
    // Source-tree helpers
    // ---------------------------------------------------------------------

    private static bool IsAllowedProviderCallFile(string relative) =>
        AllowedProviderCallFiles.Any(a =>
            relative.Replace('\\', '/').EndsWith(a.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private static bool IsCredentialResolverDoc(string relative) =>
        CredentialResolverDocFiles.Any(a =>
            relative.Replace('\\', '/').EndsWith(a.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Relative, string Text)> ActivitiesSourceFiles()
    {
        var root = ActivitiesSrcDir();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.Replace('\\', '/').StartsWith("obj/") || rel.Replace('\\', '/').StartsWith("bin/"))
                continue;
            yield return (rel, File.ReadAllText(file));
        }
    }

    private static string ReadFile(string path)
    {
        File.Exists(path).Should().BeTrue($"expected source file at {path}");
        return File.ReadAllText(path);
    }

    private static string ActivitiesSrcDir() =>
        Path.Combine(TammaElsaSrcRoot(), "Tamma.Activities");

    private static string ElsaServerSrcDir() =>
        Path.Combine(TammaElsaSrcRoot(), "Tamma.ElsaServer");

    /// <summary>
    /// Walk up from the test assembly location until we find
    /// <c>apps/tamma-elsa/src</c> (the worktree layout). Tests run from
    /// <c>apps/tamma-elsa/tests/Tamma.Activities.Tests/bin/.../</c>, so the
    /// source is a few levels up.
    /// </summary>
    private static string TammaElsaSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tamma.Activities");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "src");

            var nested = Path.Combine(dir.FullName, "apps", "tamma-elsa", "src", "Tamma.Activities");
            if (Directory.Exists(nested))
                return Path.Combine(dir.FullName, "apps", "tamma-elsa", "src");

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate apps/tamma-elsa/src by walking up from "
            + AppContext.BaseDirectory + " — the AC9 grep-gate needs the source tree.");
    }
}
