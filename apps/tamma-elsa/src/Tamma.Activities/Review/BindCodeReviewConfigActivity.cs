using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;

namespace Tamma.Activities.Review;

/// <summary>
/// Resolves the <c>CodeReview:*</c> configuration block (Story 7-1D Config, completeness
/// audit 2026-06-22 <c>CodeReview.md</c> §Missing #9) into typed outputs the workflow stores
/// into variables. Config is read via the injected <see cref="IConfiguration"/> here rather
/// than in the input-binding delegate (a <c>SetVariable</c> expression has no DI access).
/// The per-input value (when present) takes precedence over config; config takes precedence
/// over the hardcoded defaults.
///
/// <para>Fixes the spec drift where <c>WaitForFixes</c> defaulted to 24h — the resolved
/// <c>FixTimeoutMinutes</c> (default 60) is surfaced as <see cref="FixTimeoutHours"/>
/// (minutes/60, min 1h floor for the durable hour-granular wait) so the fix wait no longer
/// inherits the review timeout.</para>
/// </summary>
[Activity(
    "Tamma.Review",
    "Bind Code Review Config",
    "Resolve CodeReview:* configuration into workflow variables",
    Kind = ActivityKind.Task
)]
public class BindCodeReviewConfigActivity : CodeActivity
{
    private readonly ILogger<BindCodeReviewConfigActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Max review iterations from input (0 = use config / default)</summary>
    [Input(Description = "Max review iterations override from input (0 = unset)", DefaultValue = 0)]
    public Input<int> MaxIterationsInput { get; set; } = new(0);

    /// <summary>Merge strategy from input (defaults to config / Squash)</summary>
    [Input(Description = "Merge strategy override from input")]
    public Input<string?> MergeStrategyInput { get; set; } = new((string?)null);

    [Output(Description = "Resolved max review iterations")]
    public Output<int> MaxIterations { get; set; } = default!;

    [Output(Description = "Resolved merge strategy")]
    public Output<MergeStrategy> MergeStrategy { get; set; } = default!;

    [Output(Description = "Resolved review timeout in hours")]
    public Output<int> ReviewTimeoutHours { get; set; } = default!;

    [Output(Description = "Resolved fix timeout in hours (FixTimeoutMinutes / 60, min 1)")]
    public Output<int> FixTimeoutHours { get; set; } = default!;

    [Output(Description = "Resolved verify-CI-before-merge flag")]
    public Output<bool> VerifyCIBeforeMerge { get; set; } = default!;

    [Output(Description = "Resolved delete-branch-after-merge flag")]
    public Output<bool> DeleteBranchAfterMerge { get; set; } = default!;

    [JsonConstructor]
    public BindCodeReviewConfigActivity() { }

    public BindCodeReviewConfigActivity(
        ILogger<BindCodeReviewConfigActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var resolved = Resolve(
            _configuration,
            MaxIterationsInput.Get(context),
            MergeStrategyInput.Get(context));

        MaxIterations.Set(context, resolved.MaxIterations);
        MergeStrategy.Set(context, resolved.MergeStrategy);
        ReviewTimeoutHours.Set(context, resolved.ReviewTimeoutHours);
        FixTimeoutHours.Set(context, resolved.FixTimeoutHours);
        VerifyCIBeforeMerge.Set(context, resolved.VerifyCIBeforeMerge);
        DeleteBranchAfterMerge.Set(context, resolved.DeleteBranchAfterMerge);

        _logger?.LogInformation(
            "Resolved CodeReview config: maxIter={MaxIter}, strategy={Strategy}, " +
            "reviewTimeoutH={ReviewH}, fixTimeoutH={FixH}, verifyCi={VerifyCi}, deleteBranch={DeleteBranch}",
            resolved.MaxIterations, resolved.MergeStrategy, resolved.ReviewTimeoutHours,
            resolved.FixTimeoutHours, resolved.VerifyCIBeforeMerge, resolved.DeleteBranchAfterMerge);
    }

    /// <summary>
    /// Pure resolution of the <c>CodeReview:*</c> config (input &gt; config &gt; default).
    /// Exposed for unit testing without an Elsa context.
    /// </summary>
    public static ResolvedConfig Resolve(
        IConfiguration? configuration, int maxIterationsInput, string? mergeStrategyInput)
    {
        // MaxReviewIterations: input wins, then config, then 5.
        var maxIterations = maxIterationsInput > 0
            ? maxIterationsInput
            : configuration?.GetValue<int?>("CodeReview:MaxReviewIterations") ?? 5;
        if (maxIterations <= 0) maxIterations = 5;

        // MergeStrategy: input wins, then config, then Squash.
        var strategyRaw = !string.IsNullOrWhiteSpace(mergeStrategyInput)
            ? mergeStrategyInput
            : configuration?["CodeReview:MergeStrategy"];
        var strategy = Enum.TryParse<MergeStrategy>(strategyRaw, true, out var parsed)
            ? parsed
            : Models.MergeStrategy.Squash;

        var reviewTimeoutHours = configuration?.GetValue<int?>("CodeReview:ReviewTimeoutHours") ?? 24;
        if (reviewTimeoutHours <= 0) reviewTimeoutHours = 24;

        // FixTimeoutMinutes (default 60) → hours for the hour-granular durable wait (min 1h).
        // Fixes the spec drift where the fix wait inherited the 24h review timeout.
        var fixTimeoutMinutes = configuration?.GetValue<int?>("CodeReview:FixTimeoutMinutes") ?? 60;
        if (fixTimeoutMinutes <= 0) fixTimeoutMinutes = 60;
        var fixTimeoutHours = Math.Max(1, fixTimeoutMinutes / 60);

        var verifyCi = configuration?.GetValue<bool?>("CodeReview:VerifyCIBeforeMerge") ?? true;
        var deleteBranch = configuration?.GetValue<bool?>("CodeReview:DeleteBranchAfterMerge") ?? true;

        return new ResolvedConfig(
            maxIterations, strategy, reviewTimeoutHours, fixTimeoutHours, verifyCi, deleteBranch);
    }

    /// <summary>Immutable resolved-config bundle (for testability).</summary>
    public readonly record struct ResolvedConfig(
        int MaxIterations,
        MergeStrategy MergeStrategy,
        int ReviewTimeoutHours,
        int FixTimeoutHours,
        bool VerifyCIBeforeMerge,
        bool DeleteBranchAfterMerge);
}
