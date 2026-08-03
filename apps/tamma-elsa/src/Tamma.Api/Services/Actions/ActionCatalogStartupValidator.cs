using System.Reflection;
using System.Runtime.CompilerServices;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.Security;
using Tamma.Api.Services.Agents; // RolePhaseMap (historical namespace, Tamma.Core assembly)
using Tamma.Core;
using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-4 (AC3/AC4, D2/D7) — the fail-loud tool-vocabulary validator: the
/// check that has never existed. Tamma.Api REFUSES TO BOOT when the tool
/// vocabularies disagree with the action catalog, in either direction
/// (bidirectional, per the epic's drift rule). Four checks, each with its own
/// <c>ACTION.CATALOG.*</c> code, run to completion and thrown as ONE aggregated
/// <see cref="TammaError"/> naming every offender — a developer who has added
/// three tools sees three names, not one boot per name (the
/// <c>PromptFileLoader</c> posture).
///
/// <para><b>HOST ASYMMETRY IS DELIBERATE (D2), not an oversight:</b> this
/// hosted service is registered in <b>Tamma.Api only</b>. <c>Tamma.ElsaServer</c>
/// registers no <c>IToolExecutor</c> and no <c>IToolExecutorRegistry</c>
/// (Story 32-5 AC9 removed the tool catalog from the engine — see the comment
/// in <c>ElsaServer/Program.cs</c>), so running these checks there would throw
/// on every engine boot against an empty registry. The engine host keeps only
/// the eager <c>ActionCatalog.Validate()</c> composition call from 43-2 AC13.
/// Pinned by <c>EngineHost_DoesNotAssertToolParity</c>.</para>
/// </summary>
internal sealed class ActionCatalogStartupValidator : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly IToolExecutorRegistry _registry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ActionCatalogStartupValidator> _logger;

    public ActionCatalogStartupValidator(
        IToolExecutorRegistry registry,
        IConfiguration configuration,
        ILogger<ActionCatalogStartupValidator> logger)
    {
        _registry = registry;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Force the Story 43-2 static index build at boot rather than at first
        // request (AC4). ActionCatalog.Validate() already ran unwrapped at
        // composition; this touch is the belt for the lazy fields.
        _ = ActionCatalog.ByKey.Count;

        var violations = Check(ValidatorInputs.Live(_registry));

        // Story 42-10 (D4) — the shell/process.spawn shipped level is a PROFILE
        // input frozen into the (static) catalog at first touch. Re-derive the
        // expected level from configuration and assert the frozen catalog agrees:
        // a mismatch means the catalog was touched before ShellExecutionProfile
        // was composed (an ordering fault), and the fail-loud posture is to refuse
        // to boot rather than ship the wrong level silently.
        violations = violations
            .Concat(CheckShellProfile(
                ActionCatalog.Get(new ActionKey(ActionNamespace.Tool, ToolAction.ShellExecute.ToWire())).DefaultMinAutonomy,
                ActionCatalog.Get(new ActionKey(ActionNamespace.Effect, ExternalEffect.ProcessSpawn.ToWire())).DefaultMinAutonomy,
                _configuration.GetValue("Tools:Shell:Sandboxed", false),
                ShellExecutionProfile.IsInitialized))
            .ToList();
        if (violations.Count > 0)
        {
            throw new TammaError(
                "ACTION.CATALOG.TOOL_VOCABULARY_INVALID",
                "The action catalog failed boot validation; Tamma.Api refuses to start "
                + $"({violations.Count} violation(s)):{Environment.NewLine}"
                + string.Join(Environment.NewLine, violations.Select(v => $"  {v.Code}: {v.Detail}")),
                new Dictionary<string, object?>
                {
                    ["violations"] = violations.Select(v => $"{v.Code}: {v.Detail}").ToArray(),
                },
                retryable: false,
                severity: TammaErrorSeverity.Critical);
        }

        _logger.LogInformation(
            "Action catalog tool vocabulary validated: {ToolMemberCount} tool members, {RegistryToolCount} registered executors, {AliasCount} name aliases",
            ActionCatalog.ByKey.Keys.Count(k => k.Ns == ActionNamespace.Tool),
            _registry.GetAll().Count,
            ToolNameAliases.All.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>One violation: its <c>ACTION.CATALOG.*</c> code and the offender-naming detail.</summary>
    internal sealed record Violation(string Code, string Detail);

    /// <summary>
    /// The validator's inputs, separated from their live sources so the tests
    /// can feed doctored values through the REAL check path (D7).
    /// </summary>
    internal sealed record ValidatorInputs(
        IReadOnlyList<string> RegistryToolNames,
        IReadOnlyList<(string Role, string Name)> AdvertisedNames,
        IReadOnlyList<string> ShellToolNames,
        IReadOnlyList<Type> ExecutorImplementations)
    {
        /// <summary>The production inputs, exactly as <see cref="StartAsync"/> reads them.</summary>
        public static ValidatorInputs Live(IToolExecutorRegistry registry) => new(
            registry.GetAll().Select(e => e.ToolName).ToArray(),
            RolePhaseMap.ValidRoles
                .OrderBy(r => r, StringComparer.Ordinal)
                .SelectMany(role => DefaultAgentConfig.ForRole(role).Tools.Select(name => (role, name)))
                .ToArray(),
            ToolCallValidator.KnownShellToolNames.ToArray(),
            LiveExecutorImplementations());

        /// <summary>
        /// Every concrete <see cref="IToolExecutor"/> implementation in the two
        /// assemblies loaded in this host that declare executors —
        /// Tamma.Activities plus Tamma.Api (home of the deliberately-unregistered
        /// <c>GetAcceptanceRulesTool</c>, which <c>GetAll()</c> structurally
        /// cannot see). NOT <c>GetAll()</c> — that is the whole point of the
        /// fourth check (AC3).
        /// </summary>
        public static IReadOnlyList<Type> LiveExecutorImplementations() =>
            new[] { typeof(IToolExecutor).Assembly, typeof(ActionCatalogStartupValidator).Assembly }
                .Distinct()
                .SelectMany(GetLoadableTypes)
                .Where(t => t is { IsAbstract: false, IsInterface: false }
                            && typeof(IToolExecutor).IsAssignableFrom(t))
                .ToArray();

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null)!;
            }
        }
    }

    /// <summary>
    /// Story 42-10 (D4) — the shell-profile consistency check. Pure over its
    /// inputs (the D7 test seam): the catalog's frozen shell/process.spawn level
    /// must equal what the deployment's <c>Tools:Shell:Sandboxed</c> config
    /// implies (40 sandboxed / 80 not), and the profile must have been composed.
    /// A mismatch is an ordering fault — the catalog froze before
    /// <c>ShellExecutionProfile.Initialize</c> ran.
    /// </summary>
    internal static IReadOnlyList<Violation> CheckShellProfile(
        int catalogShellLevel, int catalogProcessSpawnLevel, bool configSandboxed, bool profileInitialized)
    {
        var violations = new List<Violation>();
        var expected = configSandboxed
            ? ShellExecutionProfile.SandboxedLevel
            : ShellExecutionProfile.UnsandboxedLevel;

        if (!profileInitialized)
        {
            violations.Add(new Violation(
                "ACTION.CATALOG.SHELL_PROFILE_UNCOMPOSED",
                "ShellExecutionProfile.Initialize was never called before catalog validation — the "
                + "host composition is missing the profile step, so the shell shipped level is "
                + "whatever the static default happened to be."));
        }

        foreach (var (name, level) in new[]
                 {
                     ("tool:shell_execute", catalogShellLevel),
                     ("effect:process.spawn", catalogProcessSpawnLevel),
                 })
        {
            if (level != expected)
            {
                violations.Add(new Violation(
                    "ACTION.CATALOG.SHELL_PROFILE_MISMATCH",
                    $"Catalogued '{name}' ships DefaultMinAutonomy {level}, but Tools:Shell:Sandboxed="
                    + $"{configSandboxed} implies {expected}. The catalog was almost certainly touched "
                    + "before ShellExecutionProfile.Initialize ran; move the Initialize call earlier in "
                    + "host composition."));
            }
        }

        return violations;
    }

    /// <summary>
    /// The four bidirectional checks (AC3). Pure over its inputs; collects every
    /// violation instead of failing fast (D7).
    /// </summary>
    internal static IReadOnlyList<Violation> Check(ValidatorInputs inputs)
    {
        var violations = new List<Violation>();

        var catalogToolKeys = ActionCatalog.ByKey.Keys
            .Where(k => k.Ns == ActionNamespace.Tool)
            .Select(k => k.Key)
            .ToHashSet(StringComparer.Ordinal);

        // ── Check 1: registry → catalog (ACTION.CATALOG.TOOL_NOT_IN_CATALOG) ──
        foreach (var name in inputs.RegistryToolNames)
        {
            if (!ToolNameAliases.TryResolve(name, out var key)
                || key.Ns != ActionNamespace.Tool
                || !catalogToolKeys.Contains(key.Key))
            {
                violations.Add(new Violation(
                    "ACTION.CATALOG.TOOL_NOT_IN_CATALOG",
                    $"Registered executor '{name}' resolves to no 'tool:*' catalog member — add a "
                    + "ToolAction member + descriptor (Tamma.Core/Actions/) and an alias entry "
                    + "(Tamma.Api/Services/Actions/ToolNameAliases.cs)."));
            }
        }

        // ── Check 2: catalog → registry, modulo the shrink-only allowlist
        //    (ACTION.CATALOG.CATALOG_TOOL_HAS_NO_EXECUTOR) ──
        var coveredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in inputs.RegistryToolNames)
        {
            if (string.Equals(name, "git_operations", StringComparison.OrdinalIgnoreCase))
            {
                // The one argument-bound split (43-2 AC8): one executor performs
                // both graded members.
                coveredKeys.Add(ToolAction.GitOperationsRead.ToWire());
                coveredKeys.Add(ToolAction.GitOperationsWrite.ToWire());
            }
            else if (ToolNameAliases.TryResolve(name, out var key) && key.Ns == ActionNamespace.Tool)
            {
                coveredKeys.Add(key.Key);
            }
        }

        var allowlisted = ToolCatalogAllowlists.NotDiRegisteredTools
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in catalogToolKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!coveredKeys.Contains(key) && !allowlisted.Contains($"tool:{key}"))
            {
                violations.Add(new Violation(
                    "ACTION.CATALOG.CATALOG_TOOL_HAS_NO_EXECUTOR",
                    $"Catalogued member 'tool:{key}' has no registered executor and no "
                    + "ToolCatalogAllowlists.NotDiRegisteredTools entry — register the executor, "
                    + "delete the catalog member, or (deliberate non-registration only) add a "
                    + "justified allowlist entry."));
            }
        }

        // ── Check 3: advertised + defensive names resolve
        //    (ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS) ──
        //
        // `key.Ns == Tool` is required here for the same reason checks 1, 2 and 4
        // require it, and its absence was a real loosening (review LOW-5,
        // 2026-07-31): once `TryResolve` grew the `mcp__*` PREFIX rule, EVERY name
        // starting `mcp__` resolved — to `effect:mcp.tool.invoke`, which is a real
        // catalog member — so `("developer", "mcp__evil__anything")` stopped being
        // a violation and Tamma.Api booted on it. These two vocabularies are the
        // FINITE ones: an agent config advertising a name, and the shell-tool
        // defensive list. A remote MCP server's tools reach the gate at runtime by
        // design and are governed there; a name baked into a shipped agent config
        // is a drift bug, and CI is the half of the D2 bargain that must still
        // catch it.
        foreach (var (role, name) in inputs.AdvertisedNames)
        {
            if (!ToolNameAliases.TryResolve(name, out var key)
                || key.Ns != ActionNamespace.Tool
                || !ActionCatalog.TryGet(key, out _))
            {
                violations.Add(new Violation(
                    "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS",
                    $"DefaultAgentConfig role '{role}' advertises tool '{name}', which resolves to no "
                    + "catalog member through ToolNameAliases — add the alias (resolution-only) or fix "
                    + "the advertised name."));
            }
        }

        var defensive = ToolCatalogAllowlists.KnownDefensiveAliases
            .Select(e => e.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in inputs.ShellToolNames)
        {
            var resolves = ToolNameAliases.TryResolve(name, out var key)
                           && key.Ns == ActionNamespace.Tool   // LOW-5 — see check 3
                           && ActionCatalog.TryGet(key, out _);
            if (!resolves && !defensive.Contains(name))
            {
                violations.Add(new Violation(
                    "ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS",
                    $"ToolCallValidator.ShellToolNames member '{name}' neither resolves through "
                    + "ToolNameAliases nor appears on ToolCatalogAllowlists.KnownDefensiveAliases — "
                    + "justify it or remove it."));
            }
        }

        // ── Check 4: every IToolExecutor implementation type maps to a catalog
        //    member (ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG). Reflection
        //    over implementations, NOT GetAll() — the deliberately-unregistered
        //    7th executor is invisible to the registry. ToolName is read off an
        //    uninitialized instance (every executor's ToolName is an
        //    expression-bodied constant — the ToolExecutorCatalogSweepTests
        //    posture), so no DI graph is needed at boot. ──
        foreach (var type in inputs.ExecutorImplementations)
        {
            string? toolName = null;
            try
            {
                toolName = (string?)typeof(IToolExecutor)
                    .GetProperty(nameof(IToolExecutor.ToolName))!
                    .GetValue(RuntimeHelpers.GetUninitializedObject(type));
            }
            catch
            {
                // fall through to the violation below — a ToolName that needs
                // constructed state is itself the defect to report.
            }

            var resolvesToTool = !string.IsNullOrWhiteSpace(toolName)
                && ToolNameAliases.TryResolve(toolName!, out var key)
                && key.Ns == ActionNamespace.Tool
                && catalogToolKeys.Contains(key.Key);
            if (!resolvesToTool)
            {
                violations.Add(new Violation(
                    "ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG",
                    $"IToolExecutor implementation '{type.FullName}' (ToolName '{toolName ?? "<unreadable>"}') "
                    + "maps to no 'tool:*' catalog member — catalogue it (ToolAction + descriptor + alias), "
                    + "and keep ToolName an expression-bodied constant so it is readable without a constructor."));
            }
        }

        return violations;
    }
}
