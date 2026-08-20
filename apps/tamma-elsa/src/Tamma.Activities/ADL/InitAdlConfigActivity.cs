using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Initializes ADL orchestrator configuration from inputs.
/// Merges configJson with direct input overrides.
/// Replaces the side-effect-laden SetVariable that was doing this work.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Init ADL Config",
    "Parse orchestrator configuration and merge input overrides",
    Kind = ActivityKind.Task
)]
public class InitAdlConfigActivity : TammaActivity
{
    public override string? EventType => "ADL.CONFIG.INIT";

    // --- Inputs ---

    [Input(Description = "Repository identifier (owner/repo)")]
    public Input<string?> Repository { get; set; } = default!;

    [Input(Description = "JSON configuration string")]
    public Input<string?> ConfigJson { get; set; } = default!;

    [Input(Description = "Override: issue labels to filter")]
    public Input<string[]?> IssueLabels { get; set; } = default!;

    [Input(Description = "Override: bot username for assignment")]
    public Input<string?> BotAssignee { get; set; } = default!;

    [Input(Description = "Override: base branch for PRs")]
    public Input<string?> BaseBranch { get; set; } = default!;

    // --- Outputs ---

    [Output(Description = "Resolved repository identifier")]
    public Output<string> ResolvedRepository { get; set; } = default!;

    [Output(Description = "Resolved issue labels")]
    public Output<string[]> ResolvedIssueLabels { get; set; } = default!;

    [Output(Description = "Resolved bot assignee")]
    public Output<string> ResolvedBotAssignee { get; set; } = default!;

    [Output(Description = "Resolved base branch")]
    public Output<string> ResolvedBaseBranch { get; set; } = default!;

    [Output(Description = "Resolved cooldown seconds")]
    public Output<int> ResolvedCooldownSeconds { get; set; } = default!;

    [Output(Description = "Resolved max issues per run")]
    public Output<int> ResolvedMaxIssuesPerRun { get; set; } = default!;

    [Output(Description = "Full parsed config JSON (for downstream use)")]
    public Output<string> ResolvedConfigJson { get; set; } = default!;

    /// <summary>
    /// <c>limits.emergencyStop</c> from the config. Shipped in
    /// <see cref="Models.OperationalLimits"/> from the start and never read by anything,
    /// so the per-instance stop was inert; the orchestrator now feeds it to
    /// <see cref="CheckLimitsActivity"/>.
    /// </summary>
    [Output(Description = "Resolved emergency-stop flag (limits.emergencyStop)")]
    public Output<bool> ResolvedEmergencyStop { get; set; } = default!;

    /// <summary>
    /// <c>limits.dailyBudgetUsd</c> — the loop's spend ceiling for the budget period. Same
    /// story as the flag above: declared, defaulted to $50, never enforced.
    /// </summary>
    [Output(Description = "Resolved spend ceiling in USD (limits.dailyBudgetUsd)")]
    public Output<decimal> ResolvedMaxSpendUsd { get; set; } = default!;

    [JsonConstructor]
    public InitAdlConfigActivity() { }

    public InitAdlConfigActivity(ILogger<InitAdlConfigActivity> logger)
    {
        Logger = logger;
    }

    protected override void Run(ActivityExecutionContext context)
    {
        // Start from defaults
        var config = new AdlConfig();

        // 2026-08-13 (engine-driven E2E): ALL the layered-override inputs are
        // OPTIONAL and must be read with GetOrDefault — .Get throws
        // "<name> is required." on a null evaluated value, and the
        // self-restart dispatch (DispatchAdlActivity) passes only configJson,
        // so every successor orchestrator instance faulted its own init here.
        // Layer 1: Parse configJson if provided
        var configJson = ConfigJson.GetOrDefault(context);
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                // 2026-08-13 (engine-driven E2E): case-INSENSITIVE on purpose.
                // Callers dispatch camelCase config ({"cooldownSeconds":3600});
                // the default case-sensitive deserialize silently ignored every
                // camelCase member, so the orchestrator ran with
                // CooldownSeconds=10 and re-selected the same still-open issue
                // every ten seconds — 25 restarts and 18 concurrent cycles for
                // one issue in a single E2E window.
                config = JsonSerializer.Deserialize<AdlConfig>(
                             configJson,
                             new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? config;
            }
            catch (JsonException ex)
            {
                Logger?.LogWarning(ex, "Failed to parse ADL config JSON, using defaults");
            }
        }

        // Layer 2: Direct input overrides
        var repoInput = Repository.GetOrDefault(context);
        if (!string.IsNullOrEmpty(repoInput))
            config.Repository = repoInput;

        var labelsInput = IssueLabels.GetOrDefault(context);
        if (labelsInput is { Length: > 0 })
            config.IssueLabels = labelsInput;

        var botInput = BotAssignee.GetOrDefault(context);
        if (!string.IsNullOrEmpty(botInput))
            config.BotAssignee = botInput;

        var branchInput = BaseBranch.GetOrDefault(context);
        if (!string.IsNullOrEmpty(branchInput))
            config.BaseBranch = branchInput;

        // Set all outputs
        ResolvedRepository.Set(context, config.Repository);
        ResolvedIssueLabels.Set(context, config.IssueLabels);
        ResolvedBotAssignee.Set(context, config.BotAssignee);
        ResolvedBaseBranch.Set(context, config.BaseBranch);
        ResolvedCooldownSeconds.Set(context, config.CooldownSeconds);
        ResolvedMaxIssuesPerRun.Set(context, config.MaxIssuesPerRun);
        ResolvedConfigJson.Set(context, JsonSerializer.Serialize(config));
        var limits = config.Limits;
        ResolvedEmergencyStop.Set(context, limits is not null && limits.EmergencyStop);
        ResolvedMaxSpendUsd.Set(context, limits?.DailyBudgetUsd ?? 0m);

        // Store resolved config for event data
        context.TransientProperties["resolvedConfig"] = config;
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
    {
        context.TransientProperties.TryGetValue("resolvedConfig", out var configObj);
        var config = configObj as AdlConfig;
        return new()
        {
            ["repository"] = config?.Repository,
            ["issueLabels"] = config?.IssueLabels,
            ["botAssignee"] = config?.BotAssignee,
            ["baseBranch"] = config?.BaseBranch,
            ["cooldownSeconds"] = config?.CooldownSeconds,
            ["maxIssuesPerRun"] = config?.MaxIssuesPerRun,
        };
    }
}
