using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Actions;

/// <summary>One action's dial-diff telemetry (Story 43-15 AC5). Both fields are
/// nullable, and null means <b>NO DATA</b> — rendered "no data" in the UI, NEVER
/// zero (Amendment 2-H's honesty rule).</summary>
/// <param name="FireCount30d">Last-30-day fire count from the mediation event
/// families, or null when this action has no fire-count SOURCE in
/// <see cref="ActionTelemetryReader.Sources"/> OR the source has zero rows (a
/// source with zero rows is indistinguishable from an unwired emitter — the H
/// chicken-and-egg — so it too renders "no data").</param>
/// <param name="ApproveRate30d">Fraction of decided authorizations granted for
/// this action in the window (<c>granted / (granted + denied)</c>), or null when
/// no grant has been decided (the grant table is structurally empty until
/// something is gated).</param>
public sealed record ActionTelemetry(int? FireCount30d, double? ApproveRate30d)
{
    /// <summary>The all-null "no data" telemetry — the default for every action
    /// with no wired source.</summary>
    public static ActionTelemetry None { get; } = new(null, null);
}

/// <summary>
/// Story 43-15 (Amendment 2-H) — the HONEST telemetry channel for the dial-diff
/// preview. Amendment 2-H verified exactly which fire-count sources exist today:
/// <list type="bullet">
/// <item>the six git mediation families and the agent-dispatch run-trigger family
/// — pinned in <see cref="Sources"/> (note <b>merge carries TWO prefixes</b>:
/// the success type <c>GIT.PR_MERGED.</c> and the failure type
/// <c>GIT.PR_MERGE.</c> differ, and BOTH count as a merge fire);</item>
/// <item>approve rates from DECIDED <c>action_authorizations</c> rows.</item>
/// </list>
///
/// <para><b>What does NOT exist, and is therefore never promised</b> (the emitters
/// are 43-9's lane, out of scope here): the <c>.ALLOWED</c> volume gate suppresses
/// SystemDefault allows (i.e. ~everything an agent-action fires), so agent-action
/// fire counts have no source; Seam B writes no decision events; there is no
/// <c>Tags->>'actionKey'</c> index. Every action not in <see cref="Sources"/>
/// reports a null fire count.</para>
///
/// <para><b>Group grants are not attributed to member actions in v1.</b> Only
/// <c>action</c>-kind decided rows feed approve rates — a <c>deploy-control</c>
/// group grant does not raise <c>agent-action:deploy</c>'s rate. Recorded here
/// rather than silently, because the field could plausibly be expected to.</para>
/// </summary>
public sealed class ActionTelemetryReader
{
    /// <summary>
    /// THE pinned source map (D8): action wire → the event-type PREFIXES whose
    /// events count as a fire of that action. Adding or removing a source is a
    /// reviewed diff, pinned by <c>ActionTelemetrySourceMapTests</c> — a future
    /// author who wires a new emitter must consciously widen this.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Sources =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["effect:git.branch.create"] = new[] { "GIT.BRANCH_CREATED." },
            ["effect:git.branch.delete"] = new[] { "GIT.BRANCH_DELETED." },
            ["effect:git.pull-request.create"] = new[] { "GIT.PR_OPENED." },
            // Merge: success (GIT.PR_MERGED.SUCCESS) and failure (GIT.PR_MERGE.FAILED)
            // are two DIFFERENT prefixes for one action (GitEventTypes.cs:38-39) — the
            // two-prefix trap. All three base-branch merge keys share the same families.
            ["effect:git.merge.dev"] = new[] { "GIT.PR_MERGED.", "GIT.PR_MERGE." },
            ["effect:git.merge.qa"] = new[] { "GIT.PR_MERGED.", "GIT.PR_MERGE." },
            ["effect:git.merge.main"] = new[] { "GIT.PR_MERGED.", "GIT.PR_MERGE." },
            ["effect:git.release.create"] = new[] { "GIT.RELEASE_CREATED." },
            ["effect:git.issue.patch"] = new[] { "GIT.ISSUE_UPDATED." },
            ["effect:agent-dispatch.run"] = new[] { "AGENT_DISPATCH.RUN_TRIGGERED." },
        };

    private readonly IEventRepository _events;
    private readonly IActionAuthorizationLedger _ledger;

    public ActionTelemetryReader(IEventRepository events, IActionAuthorizationLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(ledger);
        _events = events;
        _ledger = ledger;
    }

    /// <summary>
    /// Read telemetry for the given action wires. Every requested wire is present
    /// in the result (with <see cref="ActionTelemetry.None"/> when it has no
    /// source), so a caller never has to distinguish "absent" from "no data".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ActionTelemetry>> ReadAsync(
        Guid? tenantId, Guid? userId,
        IReadOnlyCollection<string> actionWires,
        int windowDays = 30,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var result = new Dictionary<string, ActionTelemetry>(StringComparer.Ordinal);

        // ── Fire counts — only for wires with a source; 0 renders "no data" ──
        foreach (var wire in actionWires.Distinct(StringComparer.Ordinal))
        {
            int? fireCount = null;
            if (Sources.TryGetValue(wire, out var prefixes))
            {
                var total = 0;
                foreach (var prefix in prefixes)
                {
                    total += await _events.CountByTypePrefixSinceAsync(tenantId, prefix, since)
                        .ConfigureAwait(false);
                }
                // A source with zero rows is indistinguishable from an unwired
                // emitter — "no data", never 0 (Amendment 2-H).
                fireCount = total > 0 ? total : null;
            }
            result[wire] = new ActionTelemetry(fireCount, null);
        }

        // ── Approve rates — decided action-scope grants, grouped by TargetKey ──
        var decided = await _ledger.ListDecidedSinceAsync(tenantId, userId, since, ct)
            .ConfigureAwait(false);
        foreach (var group in decided
            .Where(a => string.Equals(a.TargetKind, "action", StringComparison.Ordinal))
            .GroupBy(a => a.TargetKey, StringComparer.Ordinal))
        {
            if (!result.ContainsKey(group.Key))
            {
                continue; // an out-of-window action not in this diff
            }
            var granted = group.Count(a => a.State == "granted");
            var denied = group.Count(a => a.State == "denied");
            var total = granted + denied;
            if (total == 0)
            {
                continue; // no data — never a 0% rate
            }
            result[group.Key] = result[group.Key] with
            {
                ApproveRate30d = (double)granted / total,
            };
        }

        return result;
    }
}
