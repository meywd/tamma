using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 40-1 — the drift gate between the shipped runner template
/// (<c>apps/tamma-elsa/runner/github-actions/</c>) and the C# dispatch/collect
/// stack. The failure mode this exists to prevent is silent: a template whose
/// inputs or result keys have drifted from the dispatcher still runs green in
/// the customer's Actions and hands Tamma an unusable result.
///
/// <para>Text-based by necessity — the runner is YAML + shell and the two Api-side
/// filename defaults live in a project this one does not reference. Same posture as
/// <c>ScheduledTriggerSourcePinTests</c>: read the file, assert the literal.</para>
/// </summary>
[TestFixture]
public class RunnerContractTests
{
    /// <summary>The runner's home, relative to the apps/tamma-elsa root.</summary>
    private const string RunnerDir = "runner/github-actions";

    /// <summary>Walk up from the test bin dir to the apps/tamma-elsa root
    /// (the directory containing Tamma.sln).</summary>
    private static string ElsaRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tamma.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from within the apps/tamma-elsa tree");
        return dir!.FullName;
    }

    /// <summary>The monorepo root — where the dogfood install of the template lives.</summary>
    private static string MonorepoRoot()
    {
        var dir = new DirectoryInfo(ElsaRoot());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pnpm-workspace.yaml")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the monorepo root carries pnpm-workspace.yaml");
        return dir!.FullName;
    }

    private static string RunnerPath(params string[] parts) =>
        Path.Combine(new[] { ElsaRoot() }.Concat(RunnerDir.Split('/')).Concat(parts).ToArray());

    private static string ReadRunnerFile(params string[] parts)
    {
        var path = RunnerPath(parts);
        File.Exists(path).Should().BeTrue($"the shipped runner file is expected at {path}");
        return File.ReadAllText(path);
    }

    // ================================================================
    // AC1 — the workflow's declared inputs ARE the dispatcher's inputs
    // ================================================================

    /// <summary>
    /// Indentation-scoped read of the <c>workflow_dispatch.inputs</c> block. Deliberately
    /// small rather than a YAML dependency; if a reformat breaks it, fix the reader — do
    /// not relax the assertions it feeds.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> WorkflowDispatchInputs()
    {
        var lines = ReadRunnerFile("tamma-agent.yml").Split('\n');
        var inputs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        var inInputs = false;
        string? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0) continue;

            if (!inInputs)
            {
                if (line == "    inputs:") inInputs = true;
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent <= 4) break; // left the inputs block

            if (indent == 6 && line.TrimEnd().EndsWith(':'))
            {
                current = line.Trim().TrimEnd(':');
                inputs[current] = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else if (indent == 8 && current is not null)
            {
                var parts = line.Trim().Split(':', 2);
                if (parts.Length == 2) inputs[current][parts[0].Trim()] = parts[1].Trim().Trim('\'', '"');
            }
        }

        return inputs;
    }

    [Test]
    public async Task WorkflowInputs_AreExactly_WhatTheDispatcherSends()
    {
        // The dispatcher composes its inputs privately; go through the real
        // DispatchAsync so the pin tracks what actually goes on the wire.
        var api = new FakeTammaApiClient
        {
            OnDispatch = (_, _, _) => new AgentDispatchRunApiResponse { Success = true, DispatchedAt = DateTime.UtcNow }
        };
        await new AgentDispatchService(api).DispatchAsync(new AgentExecutionRequest(
            Repository: "acme/widgets",
            BranchName: "tamma/issue-42",
            IssueNumber: 42,
            IssueTitle: "Fix it",
            Task: "implement",
            PlanJson: "{}",
            SessionId: "sess_abc",
            AgentProvider: "claude-code",
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: 30));

        var sent = api.DispatchCalls.Single().Request.Inputs.Keys.ToHashSet(StringComparer.Ordinal);
        var declared = WorkflowDispatchInputs().Keys.ToHashSet(StringComparer.Ordinal);

        declared.Should().BeEquivalentTo(sent,
            "an input the runner does not declare is rejected by GitHub with a 422, and an "
            + "input the runner declares but Tamma never sends is dead weight in the contract");
    }

    [Test]
    public void WorkflowInputs_AreAllStringTyped()
    {
        // workflow_dispatch inputs are string-only over the REST API — a `number`
        // or `boolean` declaration makes GitHub reject Tamma's dispatch payload.
        foreach (var (name, props) in WorkflowDispatchInputs())
            props.Should().ContainKey("type").WhoseValue.Should().Be("string", $"input '{name}'");
    }

    [Test]
    public void Workflow_TriggersOnlyOnWorkflowDispatch()
    {
        var yaml = ReadRunnerFile("tamma-agent.yml");
        var triggers = Regex.Matches(yaml, @"(?m)^  ([a-z_]+):$")
            .Select(m => m.Groups[1].Value)
            .Where(t => t is "push" or "pull_request" or "pull_request_target" or "schedule" or "issue_comment")
            .ToArray();
        triggers.Should().BeEmpty(
            "an agent runner that can be triggered by anything other than a deliberate "
            + "dispatch executes agent code on attacker-controlled events");
    }

    // ================================================================
    // AC1 — the filename default is pinned at ALL SIX behavioural sites
    // ================================================================

    /// <summary>
    /// The six places that hardcode the runner's filename, each with the exact
    /// surrounding text so a rename at ONE site fails on that site alone.
    /// (Story 40-1 D8 would collapse these into one shared constant; that edit
    /// spans two projects and six files, so the story's stated alternative —
    /// pin all six — is what runs here.)
    /// </summary>
    private static (string Path, string Snippet)[] FilenameDefaultSites(string basename) => new[]
    {
        ("src/Tamma.Activities/AgentDispatch/AgentDispatchService.cs", $"? \"{basename}\""),
        ("src/Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs", $"WorkflowFileName: \"{basename}\""),
        ("src/Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs", $"new(\"{basename}\")"),
        ("src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs", $"WorkflowFileName {{ get; init; }} = \"{basename}\""),
        ("src/Tamma.Api/Services/AgentDispatch/AgentDispatchRequests.cs", $"WorkflowFileName {{ get; init; }} = \"{basename}\""),
        ("src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs", $"DefaultWorkflowFile = \"{basename}\""),
    };

    [Test]
    public void ShippedWorkflowBasename_IsTheDefault_AtAllSixSites()
    {
        var basename = Path.GetFileName(RunnerPath("tamma-agent.yml"));
        foreach (var (relative, snippet) in FilenameDefaultSites(basename))
        {
            var path = Path.Combine(ElsaRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"expected {relative}");
            File.ReadAllText(path).Should().Contain(snippet,
                $"{relative} must default to the SHIPPED runner filename — a dispatch to a "
                + "file the customer does not have fails the pre-check with WorkflowNotFound");
        }
    }

    [Test]
    public void NoSeventhSite_HardcodesTheRunnerFilename()
    {
        // Pinned sweep: the six behavioural defaults above plus two prose-only
        // mentions. A NEW file naming the runner is a seventh default that the
        // pin above would not see. Add it to one of the two lists, with a reason.
        var basename = Path.GetFileName(RunnerPath("tamma-agent.yml"));
        var proseOnly = new[]
        {
            "src/Tamma.Platforms.Abstractions/Models/WorkflowDispatchRequest.cs",
            "src/Tamma.Api/Services/AgentDispatch/AgentDispatchRequests.cs", // doc comment + the default
        };
        var expected = FilenameDefaultSites(basename).Select(s => s.Path)
            .Concat(proseOnly)
            .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.Ordinal);

        var root = ElsaRoot();
        var actual = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(basename, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToHashSet(StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(expected,
            "every production mention of the runner filename is either one of the six pinned "
            + "defaults or a documented prose mention");
    }

    // ================================================================
    // AC3 — the result artifact matches the parser, three ways
    // ================================================================

    /// <summary>snake_case names the parser reads, derived from the record itself so
    /// adding a field to <see cref="AgentResultArtifact"/> reddens the schema + script.</summary>
    private static HashSet<string> ParserReadKeys() =>
        typeof(AgentResultArtifact).GetConstructors().Single().GetParameters()
            .Select(p => Regex.Replace(p.Name!, "(?<!^)([A-Z])", "_$1").ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    [Test]
    public void GoldenFixture_RoundTripsThroughTheParser_WithEveryFieldPopulated()
    {
        var artifact = AgentResultArtifactParser.ParseResultJson(ReadRunnerFile("result.example.json"));

        artifact.Should().NotBeNull("the shipped golden fixture must parse");
        artifact!.Success.Should().BeTrue();
        artifact.Task.Should().Be("implement");
        artifact.IssueNumber.Should().Be(42);
        artifact.BranchName.Should().Be("tamma/issue-42-login-flow");
        artifact.TammaSessionId.Should().Be("adl-42-task-0");
        artifact.FilesChanged.Should().BeEquivalentTo(new[] { "src/auth/login.ts", "src/auth/login.test.ts" });
        artifact.PrNumber.Should().Be(118);
        artifact.CommitSha.Should().Be("9f2c1a7d4b6e8c05f31a2b4d6e8f0a1c3d5e7f90");
        artifact.ErrorMessage.Should().BeNull();
        artifact.AgentLogSummary.Should().NotBeNullOrEmpty();
        artifact.TokensUsed.Should().Be(48219);
        artifact.DurationSeconds.Should().Be(412);
        artifact.AgentProvider.Should().Be("claude-code");
        artifact.AgentVersion.Should().Be("1.0.0");
    }

    [Test]
    public void ResultSchema_KeySet_EqualsTheParserReadSet()
    {
        using var schema = JsonDocument.Parse(ReadRunnerFile("result.schema.json"));
        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        var properties = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        required.Should().BeEquivalentTo(ParserReadKeys(),
            "the runner must emit exactly what AgentResultArtifactParser reads — a key it "
            + "drops silently becomes a default value on Tamma's side");
        properties.Should().BeEquivalentTo(required, "every declared property is required");
    }

    [Test]
    public void CollectScript_KeySet_EqualsTheSchemaKeySet()
    {
        var script = ReadRunnerFile("scripts", "collect-results.sh");
        var match = Regex.Match(script, "(?m)^TAMMA_RESULT_KEYS=\"([^\"]+)\"");
        match.Success.Should().BeTrue("collect-results.sh declares its emitted key set as TAMMA_RESULT_KEYS");

        var scriptKeys = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        scriptKeys.Should().BeEquivalentTo(ParserReadKeys(),
            "the script self-checks its output against this list at runtime, so the list is "
            + "the contract the customer's run enforces");

        // The literal jq object the script builds must name them too — the list
        // alone would pass while the emitted JSON drifted.
        foreach (var key in scriptKeys)
            script.Should().Contain($"{key}:", $"the jq result object must set '{key}'");
    }

    [Test]
    public void Workflow_UploadsTheArtifactNameAndPath_TheAggregatorReads()
    {
        // ActionsResultAggregator lives in Tamma.Api, which this project does not
        // reference (the dependency runs the other way), so pin its constants as text.
        var aggregator = File.ReadAllText(Path.Combine(ElsaRoot(),
            "src", "Tamma.Api", "Services", "AgentDispatch", "ActionsResultAggregator.cs"));
        var artifactName = Regex.Match(aggregator, "ResultArtifactName = \"([^\"]+)\"").Groups[1].Value;
        var fileName = Regex.Match(aggregator, "ResultArtifactFileName = \"([^\"]+)\"").Groups[1].Value;
        artifactName.Should().NotBeEmpty();
        fileName.Should().NotBeEmpty();

        var yaml = ReadRunnerFile("tamma-agent.yml");
        yaml.Should().Contain($"name: {artifactName}",
            "the collector downloads the artifact BY NAME; a rename makes every run look empty");
        yaml.Should().Contain($"path: .tamma/{fileName}",
            "the collector opens the zip entry ending in this filename");
        ReadRunnerFile("scripts", "collect-results.sh").Should().Contain($"RESULT_PATH=\"${{TAMMA_DIR}}/{fileName}\"");
    }

    // ================================================================
    // AC7 / AC9 — the install is a matched, versioned set
    // ================================================================

    [Test]
    public void AllRunnerFiles_CarryTheSameVersionMarker()
    {
        var files = new[]
        {
            RunnerPath("tamma-agent.yml"),
            RunnerPath("scripts", "run-claude-code.sh"),
            RunnerPath("scripts", "collect-results.sh"),
            RunnerPath("install-runner.sh"),
        };

        var versions = files.ToDictionary(
            f => Path.GetFileName(f),
            f => Regex.Match(File.ReadAllText(f), "(?m)^# tamma-runner-version: (.+)$").Groups[1].Value.Trim());

        versions.Values.Should().OnlyContain(v => v.Length > 0, "every runner file carries the marker");
        versions.Values.Distinct().Should().ContainSingle(
            "the workflow refuses to run against scripts whose marker differs from its own, so a "
            + $"split version is a broken install: {string.Join(", ", versions.Select(kv => $"{kv.Key}={kv.Value}"))}");

        // The workflow also exports it, and the runtime check compares against that.
        ReadRunnerFile("tamma-agent.yml").Should().Contain($"TAMMA_RUNNER_VERSION: '{versions.Values.First()}'");
    }

    [Test]
    public void TammasOwnInstall_IsByteIdentical_ToTheCanonicalRunner()
    {
        // Tamma develops Tamma, so this repo is also a customer repo: its install
        // is the first consumer of the template and the cheapest drift detector.
        var repo = MonorepoRoot();
        var pairs = new (string Canonical, string Installed)[]
        {
            (RunnerPath("tamma-agent.yml"), Path.Combine(repo, ".github", "workflows", "tamma-agent.yml")),
            (RunnerPath("scripts", "run-claude-code.sh"), Path.Combine(repo, ".github", "tamma", "scripts", "run-claude-code.sh")),
            (RunnerPath("scripts", "collect-results.sh"), Path.Combine(repo, ".github", "tamma", "scripts", "collect-results.sh")),
        };

        foreach (var (canonical, installed) in pairs)
        {
            File.Exists(installed).Should().BeTrue(
                $"{installed} is Tamma's own install of the runner — re-run runner/github-actions/install-runner.sh");
            File.ReadAllText(installed).Should().Be(File.ReadAllText(canonical),
                $"{Path.GetFileName(installed)} drifted from the canonical template; "
                + "re-run install-runner.sh --upgrade instead of editing the installed copy");
        }
    }
}
