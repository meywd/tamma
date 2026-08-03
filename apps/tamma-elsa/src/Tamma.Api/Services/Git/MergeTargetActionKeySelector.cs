using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tamma.Api.Infrastructure;
using Tamma.Core.Actions;
using Tamma.Data;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 43-12 (D4) — the per-request key selector for the merge route. The merge
/// route binds all three per-target keys (<c>git.merge.dev|qa|main</c>); this picks
/// ONE by reading the PR's base branch through the git mediation seam.
///
/// <para><b>Fail-closed to <c>git.merge.main</c></b> (the highest of the three, AC3):
/// a base branch other than the literal <c>dev</c>/<c>qa</c> trunk names — including
/// <c>master</c>, <c>feature/*</c> — AND any unreadable PR resolves to
/// <c>git.merge.main</c>. The read failing is a DECISION here, never an exception:
/// an exception would ride Seam C's transient fail-OPEN arm, the opposite of what
/// AC3 requires. Every path returns a key.</para>
///
/// <para>Per the story's Out of Scope there is no per-repo trunk-name config today;
/// this maps the literal trunk names and floors everything else to the strictest
/// key — the honest v1.</para>
/// </summary>
public sealed class MergeTargetActionKeySelector : IActionKeySelector
{
    private readonly IGitMediationService _git;
    private readonly ITenantContext _tenant;
    private readonly ILogger<MergeTargetActionKeySelector> _logger;

    public MergeTargetActionKeySelector(
        IGitMediationService git, ITenantContext tenant, ILogger<MergeTargetActionKeySelector> logger)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static readonly ActionKey Dev = new(ActionNamespace.Effect, ExternalEffect.GitMergeDev.ToWire());
    private static readonly ActionKey Qa = new(ActionNamespace.Effect, ExternalEffect.GitMergeQa.ToWire());
    private static readonly ActionKey Main = new(ActionNamespace.Effect, ExternalEffect.GitMergeMain.ToWire());

    /// <summary>
    /// Map a PR base branch to a per-target merge key. Pure and total: any name other
    /// than the dev/qa trunks (and null/empty) → <c>git.merge.main</c> (fail-closed).
    /// </summary>
    public static ActionKey MapBaseBranch(string? baseBranch) =>
        baseBranch?.Trim() switch
        {
            "dev" => Dev,
            "qa" => Qa,
            _ => Main,
        };

    /// <inheritdoc />
    public async Task<ActionKey> SelectAsync(HttpContext http, IReadOnlyList<ActionKey> candidates, CancellationToken ct)
    {
        // Prefer a candidate instance from the route's own bindings (so identity is
        // shared with the metadata); fall back to the static keys.
        ActionKey Pick(ActionKey key) =>
            candidates.FirstOrDefault(c => c == key) is { } bound && bound == key ? bound : key;

        try
        {
            var owner = http.Request.RouteValues.TryGetValue("owner", out var o) ? o?.ToString() : null;
            var repoName = http.Request.RouteValues.TryGetValue("repo", out var r) ? r?.ToString() : null;
            var prRaw = http.Request.RouteValues.TryGetValue("n", out var n) ? n?.ToString() : null;

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repoName)
                || !int.TryParse(prRaw, out var prNumber))
            {
                _logger.LogWarning(
                    "Merge-target selector could not read owner/repo/PR from the route; failing closed to git.merge.main.");
                return Pick(Main);
            }

            var result = await _git.GetPullRequestAsync(
                _tenant.TenantId, $"{owner}/{repoName}", prNumber, correlationId: string.Empty, ct)
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.TargetBranch))
            {
                _logger.LogWarning(
                    "Merge-target selector could not read PR #{Pr} base branch (success={Success}); failing closed to git.merge.main.",
                    prNumber, result.Success);
                return Pick(Main);
            }

            return Pick(MapBaseBranch(result.TargetBranch));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller/host cancellation is not a governance decision — propagate so
            // Seam C's cancellation handling (not the fail-open arm) applies.
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure is a DECISION, not an evaluation error: fail closed
            // to the strictest key so it never rides Seam C's transient fail-open arm.
            _logger.LogWarning(ex,
                "Merge-target selector threw resolving the PR base branch; failing closed to git.merge.main.");
            return Pick(Main);
        }
    }
}
