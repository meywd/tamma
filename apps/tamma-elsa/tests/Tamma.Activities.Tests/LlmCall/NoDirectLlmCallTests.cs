using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AI;
using Tamma.Activities.Debug;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.TDD;
using Tamma.Api.Services.Agents; // RolePhaseMap (compiled into Tamma.Core)

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
///   <item>ZERO <c>*:ApiKey</c> / <c>*_API_KEY</c> config or env reads;</item>
///   <item>ZERO <c>/v1/messages</c> / <c>/v1/chat/completions</c> provider URLs
///         and ZERO named provider <c>HttpClient</c>s EXCEPT inside the shared
///         <see cref="InlineToolLoopRunner"/> (which executes in the API process,
///         where the key is resolved) and the <see cref="TammaApiClient"/> wire
///         client itself;</item>
///   <item>and the engine's <c>Tamma.ElsaServer</c> must be COMPLETELY clean of
///         all of the above AND register no provider credential resolver
///         (<c>AddEngineProviderCredentialResolution</c> /
///         <c>ConfigPlatformProviderCredentialResolver</c>).</item>
/// </list>
///
/// <para>Detection is a source scan keyed on the violating <em>string literal</em>
/// (the provider URL path / the config-key suffix), NOT on a specific HTTP method
/// name. A method-name gate (e.g. <c>PostAsJsonAsync</c>) is hollow — the deleted
/// callers used <c>PostAsync</c>, and a future regression could use
/// <c>SendAsync</c> / an <c>HttpRequestMessage</c> / an SDK client. Matching the
/// literal catches them all and cannot match a <c>///</c> doc-comment (those use
/// <c>&lt;c&gt;…&lt;/c&gt;</c>, never a quoted string literal).</para>
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

    // ── Violation literals (string-literal anchored; doc-comment safe) ──────
    // A quoted string literal whose value contains the provider message path.
    // The inner class excludes whitespace and angle brackets so the match cannot
    // bridge a distant quote across newlines into a doc-comment <c>/v1/messages</c>
    // (a real URL literal — "https://api.anthropic.com/v1/messages" — has neither).
    private static readonly Regex ProviderMessagesCall =
        new(@"""[^""\s<>]*/v1/messages", RegexOptions.Compiled);

    private static readonly Regex ProviderChatCompletionsCall =
        new(@"""[^""\s<>]*/v1/chat/completions", RegexOptions.Compiled);

    // Anthropic legacy text-completions endpoint.
    private static readonly Regex ProviderCompleteCall =
        new(@"""[^""\s<>]*/v1/complete", RegexOptions.Compiled);

    // A named provider HttpClient (IHttpClientFactory.CreateClient("anthropic"…)) —
    // catches an SDK/named-client provider call whose URL never appears inline.
    // Belt-and-suspenders: the URL-literal and *:ApiKey scans are the PRIMARY
    // defense. This is best-effort (a provider name list, and the real runner uses
    // an interpolated CreateClient($"llm-{provider}") that no literal regex matches).
    private static readonly Regex ProviderNamedHttpClient =
        new(@"CreateClient\(\s*""(?:anthropic|openai|openrouter|gemini|google-gemini|claude|azure-openai|zai|z\.ai)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A quoted config key ending in :ApiKey — matches a plain literal
    // ("Anthropic:ApiKey") AND an interpolated key ($"LlmProviders:{p}:ApiKey").
    // Inner class excludes whitespace/angle-brackets for the same doc-comment
    // safety as the URL literals (a config key never contains a space).
    private static readonly Regex ApiKeyConfigRead =
        new(@"""[^""\s<>]*:ApiKey""", RegexOptions.Compiled);

    // A provider API key read from the environment ("ANTHROPIC_API_KEY").
    private static readonly Regex EnvApiKeyRead =
        new(@"""[A-Z][A-Z0-9_]*_API_KEY""", RegexOptions.Compiled);

    // ── Source scans over Tamma.Activities ─────────────────────────────────

    [Test]
    public void TammaActivities_HasNoDirectProviderMessagesCall_OutsideRunnerAndWireClient()
    {
        var offenders = ActivitiesSourceFiles()
            .Where(f => !IsAllowedProviderCallFile(f.Relative))
            .Where(f => ProviderMessagesCall.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may reference /v1/messages — every LLM call routes through "
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
            "no in-engine caller may reference /v1/chat/completions. Offending files: "
            + string.Join(", ", offenders));
    }

    [Test]
    public void TammaActivities_HasNoDirectProviderCompleteCall_OutsideRunnerAndWireClient()
    {
        var offenders = ActivitiesSourceFiles()
            .Where(f => !IsAllowedProviderCallFile(f.Relative))
            .Where(f => ProviderCompleteCall.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may reference the legacy /v1/complete provider endpoint. "
            + "Offending files: " + string.Join(", ", offenders));
    }

    [Test]
    public void TammaActivities_DoesNotCreateNamedProviderHttpClient_OutsideRunnerAndWireClient()
    {
        var offenders = ActivitiesSourceFiles()
            .Where(f => !IsAllowedProviderCallFile(f.Relative))
            .Where(f => ProviderNamedHttpClient.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may construct a named provider HttpClient — provider HTTP "
            + "lives in the API-process runner. Offending files: " + string.Join(", ", offenders));
    }

    [Test]
    public void TammaActivities_HasNoApiKeyRead_Anywhere()
    {
        // The config-backed resolver that legitimately read *:ApiKey was DELETED
        // from the engine assembly in T6, so there is no longer ANY allowed reader
        // — every *:ApiKey / *_API_KEY read under Tamma.Activities is a violation.
        var offenders = ActivitiesSourceFiles()
            .Where(f => ApiKeyConfigRead.IsMatch(f.Text) || EnvApiKeyRead.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "no in-engine caller may read a provider API key (*:ApiKey config slot or "
            + "*_API_KEY env var) — credential resolution lives in Tamma.Api. Offending files: "
            + string.Join(", ", offenders));
    }

    // ── Engine-wide scan: Tamma.ElsaServer must be COMPLETELY clean ─────────

    [Test]
    public void ElsaServer_IsCompletelyFreeOfProviderCallsKeyReadsAndResolverWiring()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in ElsaServerSourceFiles())
        {
            if (ProviderMessagesCall.IsMatch(text)
                || ProviderChatCompletionsCall.IsMatch(text)
                || ProviderCompleteCall.IsMatch(text)
                || ProviderNamedHttpClient.IsMatch(text)
                || ApiKeyConfigRead.IsMatch(text)
                || EnvApiKeyRead.IsMatch(text)
                || text.Contains("AddEngineProviderCredentialResolution", StringComparison.Ordinal)
                || text.Contains("ConfigPlatformProviderCredentialResolver", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty(
            "the Elsa engine process must hold NO LLM provider key and make NO direct provider "
            + "call anywhere (not only in Program.cs): no /v1/messages or /v1/chat/completions URL, "
            + "no named provider HttpClient, no *:ApiKey / *_API_KEY read, and no credential-resolver "
            + "wiring. Offending files: " + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------------
    // Per-caller reflection proof: the cut-over types no longer expose a direct
    // keyed-LLM method (CallLlm / CallClaudeApi / CallAnthropicApi / the
    // non-mediated CallEngineCallback). They route through MediatedLlmText →
    // TammaApiClient.CallLlmAsync instead. (AC9.)
    // ---------------------------------------------------------------------

    private static readonly Type[] CutOverActivityTypes =
    {
        typeof(Tamma.Activities.LlmCall.CallLlmActivity),
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
            + "(MediatedLlmText → TammaApiClient.CallLlmAsync, or TammaApiClient directly), not a "
            + "direct keyed provider call. Offending methods: " + string.Join(", ", offenders));
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
    // Role-validity guard: every role string the cut-over callers send to the
    // call-LLM endpoint MUST be a canonical AgentRole wire or a RolePhaseMap
    // alias. The API's AgentResolverService runs AssertValidRole and returns
    // AGENT_UNRESOLVED (422) on an unknown role — which MediatedLlmText then
    // surfaces as a thrown failure. A free-text label ("debugger" / "assistant")
    // silently breaks every non-mock call; this guard makes that a red test.
    // ---------------------------------------------------------------------

    // MediatedLlmText.CompleteAsync(context, "<role>", …) — the 2nd arg literal.
    private static readonly Regex MediatedCallRole =
        new(@"CompleteAsync\(\s*[A-Za-z_]\w*\s*,\s*""([^""]+)""", RegexOptions.Compiled);

    // A blank-role default: IsNullOrWhiteSpace(<role-var>) ? "<role>" : … — matches
    // MediatedLlmText's `role` param AND CallLlmInlineActivity's `input.Role` (and
    // LlmCallWorkflow's). Scoped to role-named variables so it can't match an
    // unrelated string default (e.g. IsNullOrWhiteSpace(name) ? "x").
    private static readonly Regex BlankRoleDefault =
        new(@"IsNullOrWhiteSpace\(\s*(?:role|[\w.]*Role)\s*\)\s*\?\s*""([^""]+)""", RegexOptions.Compiled);

    // LlmCallApiRequest.Role = "<role>" — scanned ONLY in CallLlmActivity.cs,
    // which contains no chat-message Role literals (system/user/assistant/tool).
    private static readonly Regex RequestRoleAssignment =
        new(@"\bRole\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    [Test]
    public void CutOverCallers_PassOnlyResolvableAgentRoles()
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);

        // (1) every MediatedLlmText.CompleteAsync(context, "<role>", …) call site
        //     AND every blank-role default (IsNullOrWhiteSpace(<role>) ? "<role>")
        //     across Tamma.Activities — covers MediatedLlmText + CallLlmInlineActivity.
        foreach (var (_, text) in ActivitiesSourceFiles())
        {
            foreach (Match m in MediatedCallRole.Matches(text))
                roles.Add(m.Groups[1].Value);
            foreach (Match m in BlankRoleDefault.Matches(text))
                roles.Add(m.Groups[1].Value);
        }

        // (2) CallLlmActivity's LlmCallApiRequest.Role (file has no chat-message roles).
        var callLlm = ReadFile(Path.Combine(ActivitiesSrcDir(), "LlmCall", "CallLlmActivity.cs"));
        foreach (Match m in RequestRoleAssignment.Matches(callLlm))
            roles.Add(m.Groups[1].Value);

        // (3) the engine's LlmCallWorkflow role default (lives in Tamma.ElsaServer,
        //     sends its request to the same endpoint via CallLlmInlineActivity).
        var workflow = ReadFile(Path.Combine(ElsaServerSrcDir(), "Workflows", "LlmCallWorkflow.cs"));
        foreach (Match m in BlankRoleDefault.Matches(workflow))
            roles.Add(m.Groups[1].Value);

        // (4) the LlmCallWorkflowInput.Role default value, read reflectively — robust
        //     against the literal moving (this is the documented public default).
        roles.Add(new LlmCallWorkflowInput().Role);

        roles.Should().NotBeEmpty(
            "the scan must discover the cut-over role literals; an empty set means a regex broke");
        roles.Should().Contain(
            new[] { "implementer", "tester", "reviewer", "senior_developer", "developer" },
            "sanity: the known cut-over role literals must be discovered by the scan");

        foreach (var role in roles)
        {
            Action resolve = () => RolePhaseMap.AssertValidRole(RolePhaseMap.NormalizeRole(role));
            resolve.Should().NotThrow(
                $"role '{role}' is sent to POST /api/v1/llm/call — it must be a canonical AgentRole "
                + "wire or a RolePhaseMap alias, else AgentResolverService 422s and the call fails");
        }
    }

    // ---------------------------------------------------------------------
    // Source-tree helpers
    // ---------------------------------------------------------------------

    private static bool IsAllowedProviderCallFile(string relative) =>
        AllowedProviderCallFiles.Any(a =>
            relative.Replace('\\', '/').EndsWith(a.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Relative, string Text)> ActivitiesSourceFiles() =>
        EnumerateSourceFiles(ActivitiesSrcDir());

    private static IEnumerable<(string Relative, string Text)> ElsaServerSourceFiles() =>
        EnumerateSourceFiles(ElsaServerSrcDir());

    private static IEnumerable<(string Relative, string Text)> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var norm = rel.Replace('\\', '/');
            if (norm.StartsWith("obj/") || norm.StartsWith("bin/"))
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
