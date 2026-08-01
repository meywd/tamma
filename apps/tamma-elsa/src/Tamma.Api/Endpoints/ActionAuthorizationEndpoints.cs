using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Auth;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>POST …/decide body — one required field, never a defaulted write.</summary>
/// <param name="Decision"><c>granted</c> | <c>denied</c>. Missing ⇒ 400.</param>
/// <param name="Reason">Optional free-text, recorded on the row.</param>
public sealed record DecideAuthorizationRequest(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// Story 43-9 (AC13) — the HUMAN surface of the authorization ledger:
/// <c>POST /api/actions/authorizations/{id}/decide</c> and
/// <c>GET /api/actions/authorizations?state=pending</c>.
///
/// <para><b>This story adds the ROUTES, not the state machine.</b>
/// <c>IActionAuthorizationLedger.DecideAsync</c> shipped in Story 43-5 as a
/// conditional single-statement UPDATE (<c>WHERE state = 'pending'</c> and not
/// past expiry), pinned by
/// <c>ActionAssignmentStorageTests.Decide_RejectsAlreadyDecidedAndExpiredRows</c>
/// and <c>ConcurrentGrantAndDeny_ExactlyOneWins_AndTheRowMatchesTheWinner</c>.
/// The endpoint's idempotency is therefore that property surfaced as a status
/// code — a second decide on a decided row returns 409, not a silent
/// overwrite.</para>
///
/// <para><b>NO new suspend activity and NO new bookmark prefix</b> (D11).
/// <c>LifecycleBookmarks.CanonicalSuspendActivities</c> is keyed by activity
/// <c>Type</c>, so a prefix without an activity is not even representable. Grants
/// arrive here and through the six landed resume endpoints; that is what killed
/// the superseded <c>WaitForToolAuthorizationActivity</c> design wholesale.</para>
///
/// <para><b>BOTH ROUTES ARE PRINCIPAL-SCOPED</b> (adversarial review F6,
/// 2026-08-01). The LIST always was, with a comment explaining that merely
/// ENUMERATING another principal's rows is a capability disclosure — but the
/// DECIDE passed only the id, and the ledger filtered on <c>Id</c> and
/// <c>State</c> with no principal predicate. The id is handed to the caller in the
/// Seam C 409 body and the Seam E response, so in SaaS any tenant admin holding
/// one could GRANT another tenant's blocked effect: RBAC said "you may decide
/// authorizations", and nothing said "yours". The principal now rides the ledger's
/// conditional UPDATE, and a foreign row is answered with the same 409 as a
/// missing one.</para>
///
/// <para><b>RBAC</b>, matching the acceptance-rules / action-policy posture: the
/// LIST rides <c>AuthenticatedAny</c> (every role-holder needs to see what is
/// waiting on them), the DECIDE takes <c>ActionsManage</c> (tenant_owner /
/// tenant_admin — a member gets 403). Deciding an authorization is exercising the
/// autonomy policy, so it sits with editing the policy, not with reading it.</para>
///
/// <para><b>Reads go through the CP <see cref="ControlPlaneDbContext"/>
/// directly.</b> <see cref="IActionAuthorizationLedger"/> exposes
/// request/consume/decide but no LIST, and widening that interface is outside
/// this story's file scope. Recorded as a follow-up rather than hidden: the list
/// query belongs on the ledger next to the three transitions it complements.</para>
/// </summary>
public static class ActionAuthorizationEndpoints
{
    /// <summary>Recognised decisions. Anything else is a 400 — never a coercion.</summary>
    public const string DecisionGranted = "granted";
    public const string DecisionDenied = "denied";

    /// <summary>Cap on one page of the pending list.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// <c>GET /api/actions/authorizations?state=pending</c> — what is waiting on a
    /// person, scoped to the caller's own governance principal.
    /// </summary>
    public static async Task<IResult> ListAuthorizations(
        string? state,
        string? correlationId,
        int? limit,
        IGovernancePrincipalResolver principals,
        ClaimsPrincipal caller,
        IDbContextFactory<ControlPlaneDbContext> factory,
        CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(state) ? "pending" : state.Trim();
        if (wanted is not ("pending" or "granted" or "denied" or "expired" or "all"))
        {
            return Results.BadRequest(new
            {
                code = "ACTION_AUTHORIZATION.INVALID",
                error = "state must be one of pending|granted|denied|expired|all.",
            });
        }

        var gp = await principals.ResolveAsync(caller, ct).ConfigureAwait(false);
        var take = Math.Clamp(limit ?? 100, 1, MaxPageSize);

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // SCOPED TO THE PRINCIPAL, always. A tenant member must never be able to
        // enumerate another tenant's pending decisions — the row carries the
        // correlation id of a live run and the action about to happen, which is a
        // capability disclosure even without the ability to decide it.
        var query = db.ActionAuthorizations.AsNoTracking()
            .Where(a => a.TenantId == gp.TenantId && a.UserId == gp.UserId);

        if (wanted != "all") query = query.Where(a => a.State == wanted);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            var wire = correlationId.Trim();
            query = query.Where(a => a.CorrelationId == wire);
        }

        var rows = await query
            .OrderByDescending(a => a.RequestedAtUtc)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        return Results.Ok(new
        {
            state = wanted,
            count = rows.Count,
            authorizations = rows.Select(a => new
            {
                id = a.Id,
                correlationId = a.CorrelationId,
                targetKind = a.TargetKind,
                targetKey = a.TargetKey,
                state = a.State,
                requestedAtUtc = a.RequestedAtUtc,
                decidedAtUtc = a.DecidedAtUtc,
                decidedByUserId = a.DecidedByUserId,
                expiresAtUtc = a.ExpiresAtUtc,
                consumedAtUtc = a.ConsumedAtUtc,
                autonomyLevelAtRequest = a.AutonomyLevelAtRequest,
                reason = a.Reason,
                // A row can be past its expiry while still saying `pending` —
                // expiry is enforced by the transition predicates, not by a
                // sweeper — so the surface says so rather than showing a decision
                // button that will 409.
                expired = a.ExpiresAtUtc is DateTime e && e <= now,
            }),
        });
    }

    /// <summary>
    /// <c>POST /api/actions/authorizations/{id}/decide</c> — a person grants or
    /// denies one pending authorization.
    /// </summary>
    public static async Task<IResult> Decide(
        Guid id,
        DecideAuthorizationRequest body,
        IActionAuthorizationLedger ledger,
        IGovernancePrincipalResolver principals,
        ActionGateEventsService events,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Decision))
        {
            return Results.BadRequest(new
            {
                code = "ACTION_AUTHORIZATION.INVALID",
                error = "decision is required and must be 'granted' or 'denied'. A missing field "
                    + "is never a defaulted write on a safety surface.",
            });
        }

        var wanted = body.Decision.Trim();
        if (wanted is not (DecisionGranted or DecisionDenied))
        {
            return Results.BadRequest(new
            {
                code = "ACTION_AUTHORIZATION.INVALID",
                error = $"decision must be '{DecisionGranted}' or '{DecisionDenied}'.",
            });
        }

        var actorUserId = caller.GetUserId();
        if (actorUserId is not Guid actor)
        {
            // The ledger records WHO decided; a decision with no identifiable
            // decider is not an audit trail. Fail rather than write Guid.Empty.
            return Results.BadRequest(new
            {
                code = "ACTION_AUTHORIZATION.NO_ACTOR",
                error = "the caller has no resolvable user id, so the decision could not be "
                    + "attributed to a person.",
            });
        }

        // F6 (adversarial review, 2026-08-01) — resolved BEFORE the transition,
        // because the transition is now scoped to it. See the ledger's DecideAsync
        // remarks: the id travels in the Seam C 409 body and the Seam E response,
        // so an unscoped decide let any holder of a guid grant another principal's
        // blocked effect.
        var gp = await principals.ResolveAsync(caller, ct).ConfigureAwait(false);

        var granted = wanted == DecisionGranted;
        var row = await ledger
            .DecideAsync(gp.TenantId, gp.UserId, id, granted, actor, body.Reason, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            // The ledger's CAS returned 0 rows: missing, already decided, past
            // expiry, owned by ANOTHER governance principal, or it lost a
            // concurrent grant-vs-deny race. All five are 409 — the request
            // conflicts with the row's current state — and NOT 404/403, either of
            // which would turn the endpoint into an existence oracle for another
            // principal's correlation ids. That matters MORE for the foreign-owner
            // case than for the others: a distinct status for "exists but is not
            // yours" would confirm the existence of a live run in another tenant,
            // and the guids are handed out in denial bodies. One indistinguishable
            // answer for all five is the non-disclosing choice.
            return Results.Conflict(new
            {
                code = "ACTION_AUTHORIZATION.NOT_PENDING",
                error = "no pending, unexpired authorization with that id could be decided. It "
                    + "may already have been decided, have expired, or not exist.",
                id,
            });
        }

        if (!granted)
        {
            await events.EmitAuthorizationDeniedAsync(
                gp.TenantId, gp.UserId, row.TargetKey, row.CorrelationId, row.Id)
                .ConfigureAwait(false);
        }

        return Results.Ok(new
        {
            id = row.Id,
            state = row.State,
            correlationId = row.CorrelationId,
            targetKind = row.TargetKind,
            targetKey = row.TargetKey,
            decidedAtUtc = row.DecidedAtUtc,
            decidedByUserId = row.DecidedByUserId,
            expiresAtUtc = row.ExpiresAtUtc,
            reason = row.Reason,
        });
    }
}
