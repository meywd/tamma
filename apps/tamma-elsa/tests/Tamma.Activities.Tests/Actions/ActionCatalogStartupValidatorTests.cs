using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.AcceptanceRules;
using Tamma.Api.Services.Actions;
using Tamma.Core;

using Violation = Tamma.Api.Services.Actions.ActionCatalogStartupValidator.Violation;
using ValidatorInputs = Tamma.Api.Services.Actions.ActionCatalogStartupValidator.ValidatorInputs;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-4 AC3/AC4/AC7 — the fail-loud tool-vocabulary startup validator.
/// One test per throw code, each feeding a doctored input through the REAL
/// check path and asserting the code AND that the message names the offending
/// symbol; plus the aggregation guarantee (three faults → three names, one
/// throw), the live green case against the real six-executor registry, and the
/// deliberate host asymmetry (the engine registers no tool catalog).
/// </summary>
[TestFixture]
public class ActionCatalogStartupValidatorTests
{
    /// <summary>The production inputs, as StartAsync reads them (live sources).</summary>
    private static ValidatorInputs LiveInputs() => ValidatorInputs.Live(RealRegistry());

    /// <summary>
    /// The real six-executor registry, composed exactly as Tamma.Api's DI does
    /// (the six AddSingleton&lt;IToolExecutor, …&gt; lines in Program.cs).
    /// </summary>
    private static ToolExecutorRegistry RealRegistry()
    {
        var config = new ConfigurationBuilder().Build();
        return new ToolExecutorRegistry(
            new IToolExecutor[]
            {
                new FileReadTool(NullLogger<FileReadTool>.Instance, config),
                new FileWriteTool(NullLogger<FileWriteTool>.Instance, config),
                new SearchCodeTool(NullLogger<SearchCodeTool>.Instance, config),
                new ShellExecuteTool(NullLogger<ShellExecuteTool>.Instance, config),
                new GitOperationsTool(NullLogger<GitOperationsTool>.Instance, config),
                new RunTestsTool(NullLogger<RunTestsTool>.Instance, config),
            },
            NullLogger<ToolExecutorRegistry>.Instance);
    }

    // ── The load-bearing green case ─────────────────────────────────────────

    [Test]
    public void Validator_passes_on_the_real_composition()
    {
        var violations = ActionCatalogStartupValidator.Check(LiveInputs());

        violations.Should().BeEmpty(
            "the shipped tool vocabularies and the catalog must agree — a violation here is a "
            + "real drift, not a test artifact: "
            + string.Join("; ", violations.Select(v => $"{v.Code}: {v.Detail}")));
    }

    [Test]
    public async Task StartAsync_boots_green_on_the_real_registry_and_throws_on_a_doctored_one()
    {
        // Green: the hosted service completes against the real six executors.
        var validator = new ActionCatalogStartupValidator(
            RealRegistry(), NullLogger<ActionCatalogStartupValidator>.Instance);
        await validator.StartAsync(CancellationToken.None);

        // Red: an uncatalogued executor refuses the boot AT STARTUP, and the
        // aggregate error carries the per-check code and the offender.
        var doctored = new ToolExecutorRegistry(
            new IToolExecutor[] { new UncataloguedTool() },
            NullLogger<ToolExecutorRegistry>.Instance);
        var failing = new ActionCatalogStartupValidator(
            doctored, NullLogger<ActionCatalogStartupValidator>.Instance);

        var act = () => failing.StartAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Message.Should()
            .Contain("ACTION.CATALOG.TOOL_NOT_IN_CATALOG").And.Contain("frobnicate");
    }

    // ── One test per throw code (AC7) ───────────────────────────────────────

    [Test]
    public void Boot_Throws_WhenExecutorHasNoCatalogMember()
    {
        var inputs = LiveInputs() with
        {
            RegistryToolNames = LiveInputs().RegistryToolNames.Append("frobnicate").ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Should().ContainSingle(v => v.Code == "ACTION.CATALOG.TOOL_NOT_IN_CATALOG")
            .Which.Detail.Should().Contain("frobnicate");
    }

    [Test]
    public void Boot_Throws_WhenCatalogToolHasNoExecutor()
    {
        // Drop git_operations from the registry: both graded catalog members
        // lose their executor and neither is allowlisted.
        var inputs = LiveInputs() with
        {
            RegistryToolNames = LiveInputs().RegistryToolNames
                .Where(n => n != "git_operations").ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Where(v => v.Code == "ACTION.CATALOG.CATALOG_TOOL_HAS_NO_EXECUTOR")
            .Should().HaveCount(2)
            .And.Contain(v => v.Detail.Contains("tool:git_operations.read"))
            .And.Contain(v => v.Detail.Contains("tool:git_operations.write"));
    }

    [Test]
    public void Boot_Throws_WhenAdvertisedNameIsUnresolvable()
    {
        var inputs = LiveInputs() with
        {
            AdvertisedNames = LiveInputs().AdvertisedNames
                .Append(("developer", "Frobnicate")).ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Should().ContainSingle(v => v.Code == "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS")
            .Which.Detail.Should().Contain("Frobnicate").And.Contain("developer");
    }

    /// <summary>
    /// <b>Review LOW-5 (2026-07-31).</b> The <c>mcp__*</c> PREFIX rule added to
    /// <c>ToolNameAliases.TryResolve</c> made every name beginning <c>mcp__</c>
    /// resolve — to a real catalog member — which silently switched this check off
    /// for that whole family: <c>("developer", "mcp__evil__anything")</c> stopped
    /// producing a violation and Tamma.Api booted on it. Checks 1, 2 and 4 all
    /// require <c>key.Ns == Tool</c>; check 3 did not, and that asymmetry was the
    /// hole. An MCP name reaching the GATE at runtime is the design; an MCP name
    /// baked into a shipped agent config is drift, and CI is the half of the D2
    /// bargain that has to catch it.
    /// </summary>
    [TestCase("mcp__evil__anything")]
    [TestCase("mcp__server__tool")]
    [TestCase("MCP__Server__Tool")]
    public void Boot_Throws_WhenAnAdvertisedNameIsAnMcpName(string name)
    {
        var inputs = LiveInputs() with
        {
            AdvertisedNames = LiveInputs().AdvertisedNames
                .Append(("developer", name)).ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Should().ContainSingle(v => v.Code == "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS")
            .Which.Detail.Should().Contain(name);
    }

    /// <summary>The same hole on the shell-tool vocabulary (LOW-5).</summary>
    [Test]
    public void Boot_Throws_WhenAShellToolNameIsAnMcpName()
    {
        var inputs = LiveInputs() with
        {
            ShellToolNames = LiveInputs().ShellToolNames.Append("mcp__evil__anything").ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Should().ContainSingle(v => v.Code == "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS")
            .Which.Detail.Should().Contain("mcp__evil__anything");
    }

    /// <summary>
    /// The control: the LIVE inputs are still clean, so restoring the strictness
    /// did not just make the real vocabularies unbootable.
    /// </summary>
    [Test]
    public void TheLiveVocabularies_StillProduceNoViolations()
    {
        ActionCatalogStartupValidator.Check(LiveInputs()).Should().BeEmpty();
    }

    [Test]
    public void Boot_Throws_WhenAShellToolNameIsNeitherResolvableNorJustified()
    {
        var inputs = LiveInputs() with
        {
            ShellToolNames = LiveInputs().ShellToolNames.Append("mystery_shell").ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Should().ContainSingle(v => v.Code == "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS")
            .Which.Detail.Should().Contain("mystery_shell");
    }

    [Test]
    public void Boot_Throws_WhenAnImplementationTypeIsUncatalogued()
    {
        var inputs = LiveInputs() with
        {
            ExecutorImplementations = LiveInputs().ExecutorImplementations
                .Append(typeof(UncataloguedTool)).ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Should().ContainSingle(v => v.Code == "ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG")
            .Which.Detail.Should().Contain(nameof(UncataloguedTool));
    }

    // ── Aggregation (D7) ────────────────────────────────────────────────────

    [Test]
    public void Validator_ReportsEveryViolationInOneThrow()
    {
        // Three simultaneous faults → three violations in one result set: a
        // developer who has added three tools sees three names, not one boot
        // per name.
        var live = LiveInputs();
        var inputs = live with
        {
            RegistryToolNames = live.RegistryToolNames.Append("frobnicate").ToArray(),
            AdvertisedNames = live.AdvertisedNames.Append(("developer", "Widget")).ToArray(),
            ExecutorImplementations = live.ExecutorImplementations.Append(typeof(UncataloguedTool)).ToArray(),
        };

        var violations = ActionCatalogStartupValidator.Check(inputs);

        violations.Select(v => v.Code).Should().BeEquivalentTo(new[]
        {
            "ACTION.CATALOG.TOOL_NOT_IN_CATALOG",
            "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS",
            "ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG",
        });
    }

    // ── The reflection sweep sees the deliberately-unregistered 7th (D4) ────

    [Test]
    public void AllIToolExecutorImplementations_HaveACatalogMember()
    {
        var implementations = ValidatorInputs.LiveExecutorImplementations();

        implementations.Should().Contain(typeof(GetAcceptanceRulesTool),
            "the deliberately-unregistered 7th executor is invisible to GetAll(); the type sweep "
            + "is the check that sees it (Story 39-5 D6 / 43-4 D4)");

        var violations = ActionCatalogStartupValidator
            .Check(LiveInputs())
            .Where(v => v.Code == "ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG");
        violations.Should().BeEmpty();
    }

    // ── Host asymmetry (AC4, D2) ────────────────────────────────────────────

    [Test]
    public void EngineHost_DoesNotAssertToolParity()
    {
        // Tamma.ElsaServer registers no IToolExecutor and no registry (Story
        // 32-5 AC9) — running the tool checks there would throw on every engine
        // boot. The asymmetry is DELIBERATE: the engine keeps only the eager
        // ActionCatalog.Validate() composition call from 43-2 AC13. Pinned as a
        // source assertion so a "why is this only in one host?" refactor
        // re-reads the reason instead of unifying the hosts.
        var engineProgram = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Tamma.ElsaServer", "Program.cs"));

        engineProgram.Should().NotContain("ActionCatalogStartupValidator",
            "the engine host must not run the tool-vocabulary checks (empty registry ⇒ guaranteed boot failure)");
        engineProgram.Should().NotContain("AddActionCatalogGovernance",
            "the governance DI extension is Tamma.Api-only — see ActionCatalogGovernanceServiceCollectionExtensions");
        engineProgram.Should().Contain("ActionCatalog.Validate()",
            "the engine keeps the 43-2 AC13 eager catalog validation (the catalog touch)");
    }

    [Test]
    public void ApiHost_WiresTheValidatorThroughTheGovernanceExtension()
    {
        // The Tamma.Api host reaches the validator via
        // AddAgentResolverServices → AddActionCatalogGovernance (both DI
        // extension files); Program.cs itself already calls
        // AddAgentResolverServices. Source-pinned at the extension level so the
        // wiring cannot be silently dropped.
        var agentResolverExtension = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Tamma.Api", "Extensions",
            "AgentResolverServiceCollectionExtensions.cs"));
        agentResolverExtension.Should().Contain("AddActionCatalogGovernance");

        var apiProgram = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Tamma.Api", "Program.cs"));
        apiProgram.Should().Contain("AddAgentResolverServices");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A doctored executor with no catalog member.</summary>
    private sealed class UncataloguedTool : IToolExecutor
    {
        public string ToolName => "frobnicate";
        public string Description => "not catalogued";
        public Dictionary<string, object> InputSchema => new();
        public Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolExecutionResult(toolCallId, ToolName, false, "n/a", 0));
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
