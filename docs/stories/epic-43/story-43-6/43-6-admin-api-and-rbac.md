# Story 43-6: Admin API + RBAC for the Action Catalog

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **tenant admin** (SaaS) or **the sole user** (self-hosted),
I want an HTTP surface that lists every action and group with its effective assignment *at a level I choose to view*, and lets me change one field at a time,
So that I can decide what the system does by itself and what waits for a person — without a save that silently resets a field I never touched.

## Priority

P0 — Story 43-7 (the admin UI) has nothing to render without it, and it is the only write path into
Story 43-5's storage. It is also where the epic's single new permission lands, and a permission registered
in two of the three required places fails silently in exactly one environment.

## Architectural Context (READ FIRST)

### A new permission needs THREE places, and the third is the one people miss

The house convention (six stores have followed it verbatim) is `<noun>:manage` → `["admin", "owner"]`:

1. **`apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`** — a `Matrix` entry. The list is 18 keys today;
   `acceptance-rules:manage` is the last, at `:80`, with a comment explaining why owner-only
   `settings:manage` would 403 every tenant_admin. `actions:manage` goes immediately after it with the same
   explanation.
2. **`apps/tamma-elsa/src/Tamma.Api/Program.cs:1518-1696`** — `options.AddPolicy("ActionsManage", …)`,
   beside `AcceptanceRulesManage` at `:1618`.
3. **`apps/tamma-elsa/src/Tamma.Api/Program.cs:1724-1726`** — the **Development-permissive literal string
   array**. When no JWT secret is configured in Development, the app re-registers every named policy with
   `AllowAnonymousRequirement` by iterating a hardcoded 22-name array. A policy missing from that array is
   never registered in that branch, and the first request to a route using it fails with a policy-not-found
   error. This is a silent, environment-specific failure that no other test catches.

### Correction to the design: `PermissionsMatrixTests` has no count to increment

The design says "`PermissionsMatrixTests` count goes 18 → 19". **It does not — there is no count.**
`apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/PermissionsMatrixTests.cs` is entirely `[TestCase]`-driven, one
literal permission string per case, and it asserts nothing about the size or the key set of
`Permissions.Matrix`. Adding a permission and forgetting the tests is currently invisible. This story
therefore **adds the pin that does not exist**: an exact key-set assertion over `Permissions.Matrix.Keys`
(19 names, symmetric-diff failure message), alongside the new `[TestCase]` rows for `actions:manage`.

`acceptance-rules:manage` is **not** deleted or merged. `api_keys.Permissions` is a `text[]` accepted
free-form with zero validation at creation (`AdminApiKeysEndpoints.cs:63`), so any already-issued key
carrying that string would start 403-ing the moment the key disappeared from the matrix.
`AcceptanceRulesEndpointsTests.cs:192-197`'s `ContainKey("acceptance-rules:manage")` stays intact.
`tools:manage` is never created — one permission for the whole gating plane.

### Route ordering: literals before parameterized

`apps/tamma-elsa/src/Tamma.Api/Program.cs:2726-2733` carries the warning in prose: the literal `/defaults`
route MUST be registered before `/{documentTypeKey}` or "defaults" is swallowed by the parameter. This
story's group has the same shape and more of it — `/policy/reset` and `/authorizations` are literals under a
group that also has `/policy/actions/{ns}/{key}`. Register literals first.

Reads ride `AuthenticatedAny`, for the same deliberate reason the acceptance-rules and convention stores do
(rationale in the comment at `Program.cs:2716-2727`): every role-holder and the orchestrator need the
effective policy, not just admins. Only writes take `ActionsManage`.

### Single-field PUTs — this is the 43-0 bug class, one layer down

`apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs:22-25` declares

```csharp
[property: JsonPropertyName("acceptorRequirement")] AcceptorRequirement AcceptorRequirement
    = AcceptorRequirement.Any
```

— a **defaulted trailing field on a full-object PUT**. The dashboard's edit dialog omits it from the save
body, so every admin save silently resets `design` from human-required back to `any`. Story 43-0 fixes that
instance. This story must not create another: **every write endpoint here takes a single-field, non-defaulted,
required DTO**, so a missing field is a 400 rather than a silent reset. A safety catalog that silently reset
a gate would be materially worse than a prompt store that does.

### `automation:*` targets take only two states

A background sweeper cannot suspend for a human — there is nobody on that path to wait for. Every
`automation:*` descriptor is marked non-escalatable, so the only enforcing outcome is `Denied`. A mid-range
threshold on such a target would therefore **silently behave as Deny** while displaying as "human below
level N". The API rejects it.

### Mode split lives in the handler body, repeated

`AcceptanceRulesEndpoints.cs:37,77,121,152` repeats
`modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId` inline in every handler;
`PromptEndpoints.cs:252` does the same. Six stores do it this way and **no shared helper exists**.
Introducing the repo's first such helper inside a safety story would be a half-migration. Repeat it, and
mitigate with a per-endpoint test.

## Acceptance Criteria

1. **The route group exists, registered immediately after the acceptance-rules group
   (`Program.cs:2731-2734`), literals before parameterized:**

   ```
   GET    /api/actions/dial                                     AuthenticatedAny
   GET    /api/actions/catalog                                  AuthenticatedAny
   GET    /api/actions/policy            ?level=NN              AuthenticatedAny
   POST   /api/actions/policy/reset                             ActionsManage
   PUT    /api/actions/policy/groups/{group}/threshold          ActionsManage
   PUT    /api/actions/policy/groups/{group}/enforce            ActionsManage
   DELETE /api/actions/policy/groups/{group}                    ActionsManage
   GET    /api/actions/policy/actions/{ns}/{key}                AuthenticatedAny
   PUT    /api/actions/policy/actions/{ns}/{key}/threshold      ActionsManage
   PUT    /api/actions/policy/actions/{ns}/{key}/enforce        ActionsManage
   PUT    /api/actions/policy/actions/{ns}/{key}/enabled        ActionsManage
   PUT    /api/actions/policy/actions/{ns}/{key}/roles          ActionsManage
   DELETE /api/actions/policy/actions/{ns}/{key}                ActionsManage
   GET    /api/actions/authorizations                           AuthenticatedAny
   POST   /api/actions/authorizations/{id:guid}/decide          ActionsManage
   ```

   Tested by `RouteOrder_LiteralsBeatParameterized`: `GET /api/actions/policy/reset`-shaped literals are not
   captured by a parameterized sibling.

2. **Every write DTO is single-field, required, and non-defaulted.**
   `SetThresholdRequest(int MinAutonomy)`, `SetEnforceRequest(bool Enforce)`, `SetEnabledRequest(bool Enabled)`,
   `SetRolesRequest(string[]? AllowedRoles)`. No property carries a default value; a body missing the field
   returns **400**, never a defaulted write. Pinned by `PutThreshold_DoesNotResetEnforceEnabledOrRoles` —
   set all four fields, then PUT only the threshold, then assert the other three are unchanged in storage.

3. **`actions:manage` is registered in all THREE places**, and the third is tested.
   `ActionsPolicyRegistrationTests.ActionsManage_IsInDevelopmentPermissiveArray` reads the literal array at
   `Program.cs:1724-1726` (or asserts the policy resolves in a Development-without-JWT host) and fails if
   the name is absent.

4. **The permission matrix gains a key-set pin.** `PermissionsMatrixTests` gains an exact
   `Permissions.Matrix.Keys` assertion (19 names, symmetric-diff message naming the missing/extra key) plus
   `[TestCase("actions:manage")]` rows proving member → false, admin → true, owner → true.
   `acceptance-rules:manage` remains in the matrix and its existing assertions are untouched.

5. **`GET /api/actions/policy?level=NN` returns a server-computed, level-parameterized view.**
   Per group and per action: `minAutonomy`, `enforce`, `enabled`, `allowedRoles`, `source` (provenance),
   `risk`, `title`, `summary`, `siteKey`, and the three **server-computed** fields:
   - `automatedAtLevel` — computed by calling the **same** method the gate calls
     (`ActionPolicyValue.IsAutomatedAt`), so the UI's greying rule cannot drift from the enforcement rule;
   - `editable` — always `true` (per S3: a row automated at the previewed level is still editable, because
     setting a threshold that only matters at a future lower floor is the entire point);
   - `enforcementSites` — the count of seams that will actually enforce this member, so the UI can say
     "not yet enforced anywhere" for a member whose route is still on the ungoverned backlog, instead of
     implying protection that does not exist.
   The `dial` block (`min`, `max`, `alwaysHuman`, `default`, `current`) is included so the UI never
   hardcodes bounds.

6. **`GET /api/actions/dial` and `GET /api/actions/catalog`** publish `AutonomyDial` and the full catalog
   (key, namespace, title, summary, group, risk, reversible, `escalatableToHuman`, `siteKey`) so no client
   needs a local copy of either vocabulary.

7. **Validation is exact and rejects rather than coerces.**
   - `MinAutonomy` must satisfy `AutonomyDial.IsValidThreshold` (or equal `AutonomyDial.AlwaysHuman`) → else
     400 `ACTION_POLICY.INVALID`.
   - `{ns}/{key}` is parsed via `ActionCatalog.TryGet`, **case-sensitive ordinal** — bad casing is a 400,
     not a coercion (`EnumWire.cs:39` posture). `UnknownWire_400`, `WrongCasing_400`.
   - `{group}` is parsed against `ActionGroup`'s wire set with the same posture.
   - **`automation:*` targets accept only `AutonomyDial.Min` or `AutonomyDial.AlwaysHuman`.** A mid-range
     value returns 400 `ACTION_POLICY.INVALID` with a reason naming the non-escalatable descriptor.
     `AutomationTarget_RejectsMidRangeThreshold`.

8. **Writes key on the right principal per mode**, using the inline mode split (never a shared helper).
   `SingleUser_KeysOnUserId`, `SaaS_KeysOnTenantId`, and per-endpoint
   `SaaS_caller_never_reads_a_user_scoped_row`.

9. **RBAC is enforced on writes only.** A SaaS `member` calling any PUT/DELETE/POST gets **403**
   (`Member_Gets403OnWrite`); the same member calling any GET succeeds. Platform-ceiling rows
   (neither principal key set) are **not writable through this surface** in v1 — the endpoints always write
   a principal-scoped row; a platform-ceiling write path is out of scope.

10. **DELETE removes the row and resolution falls to the next tier** — the `AcceptanceRulesService.DeleteAsync`
    semantic. `POST /policy/reset` deletes every row for the calling principal.
    `Delete_FallsBackToNextTier` asserts the resolved value after delete equals the group/system value, not a
    zeroed one.

11. **Every write emits an audit event** through Story 43-5's `ActionGateEventsService` family
    (assignment-change events carrying `{actionKey|group, oldValue, newValue, field, principal, actor}`),
    and a write whose audit append fails still surfaces the write result — the assignment change is the
    durable fact, the event is best-effort, matching the `<Feature>EventsService` template.

12. **`POST /api/actions/authorizations/{id}/decide`** transitions a `pending` ledger row to
    `granted`/`denied`, records `decided_by_user_id` and `decided_at_utc`, and rejects a decision on an
    already-decided or expired row with 409.

## Dependencies

- **Story 43-5** — `IActionAssignmentRepository`, `IActionAuthorizationLedger`,
  `IGovernancePrincipalResolver`, `AutonomyGateEvaluator` (for the resolved view), `ActionGateEventsService`.
  **Blocking**; this story is the HTTP face of that storage.
- **Story 43-1** — `AutonomyDial` for `GET /dial` and `IsValidThreshold`. Blocking.
- **Story 43-2 / 43-3** — `ActionCatalog.TryGet`, `ActionGroup`, descriptors (title/summary/risk/
  `escalatableToHuman`) for `GET /catalog` and the resolved view. Blocking.
- **Existing, verified:** `Permissions.cs:80` and the 18-key matrix; `Program.cs:1518-1696` (22 policies),
  `:1724-1726` (the Development-permissive array), `:2716-2734` (the acceptance-rules group, its read-gate
  rationale and its route-ordering warning); `AcceptanceRulesEndpoints.cs:37,77,121,152` (the inline mode
  split); `AcceptanceRulesDtos.cs:22-25` (the defaulted-field bug class to avoid);
  `PermissionsMatrixTests.cs`; `AdminApiKeysEndpoints.cs:63` (free-form api-key permissions).
- **Feeds:** Story 43-7 (the UI binds to exactly these shapes), Story 43-9 (the `decide` endpoint and the
  ledger become live when the seams land).

## Out of Scope

- **The admin UI.** Story 43-7.
- **Any enforcement seam.** Story 43-9 — this story writes policy; nothing reads it at a call site yet.
- **A platform-ceiling write path.** Ceiling rows exist in storage (43-5) and are honoured by the evaluator,
  but there is no endpoint to author them in v1.
- **Deleting or merging `acceptance-rules:manage`.** Deliberately retained (free-form api-key permissions).
- **A shared mode-split helper.** Six stores repeat it inline; introducing the first helper here would be a
  half-migration inside a safety story.
- **Two-person approval for an assignment change.** No two-person mechanism exists anywhere in the repo;
  it is a new capability, recorded as an epic open question.
- **Validating `api_keys.Permissions` against the matrix.** Pre-existing hole (`AdminApiKeysEndpoints.cs:63`);
  it means `actions:manage` is self-grantable by anyone who can mint a key. Recorded as a risk, not fixed here.

## Estimated Effort

3 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
