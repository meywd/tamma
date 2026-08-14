# 2026-08-13 — engine defects surfaced by the engine-driven E2E

> Scope note: items 2 and 7 are OPEN composition gaps; everything else is a
> REAL runtime defect the E2E surfaced in the autonomous loop itself — every
> one shipped latent because no test had ever driven the loop through a real
> engine process end-to-end. All non-open items are FIXED in the same change
> that added the E2E.

**Status:** open follow-ups. Found while booting the REAL `Tamma.ElsaServer`
binary under `ASPNETCORE_ENVIRONMENT=Development` for the engine-driven
autonomous E2E (`EngineFullStackFixture`) — Development turns on
`ValidateOnBuild`/`ValidateScopes`, which the deployed engine (Production)
never runs, so both defects ship latent today.

## 1. `LifecycleReEntryService` is unresolvable in every engine composition — FIXED (HTTP seam)

`Program.cs` registered the REAL `ILifecycleReEntryService` (39-10/39-11) as
the default, but its dependency `IDocumentInstanceRepository` is only
registered by `AddTammaData(...)` — which **only Tamma.Api calls**. The engine
never registers it, so any `ComputeReEntryPositionActivity` execution in the
deployed engine faulted at service resolution (masked to a per-activity
incident by `ContinueWithIncidentsStrategy`, so lifecycles silently lost crash
re-entry).

**Why "just disable it" was NOT enough (E2E runs 29–30):** the 39-14
`plan-review` shim is a latest-accepted READ (`FetchLatestAcceptedDocument` →
approved/needsHuman). With the Null seam the read can never see the plan the
lifecycle just accepted, so **every** engine-driven cycle terminated
needs-human — re-entry-disabled is not a degraded mode there, it is a
structurally-dead autonomous loop.

**Fix (same change):** the engine host now defaults to
`HttpLifecycleReEntryService` — the latest-accepted read over the API's own
39-11 HTTP surface (`GET /api/documents/issues/{issueId}/latest` + `GET
/api/documents/{documentId}`), deliberately coarser than the real service
(accepted → `Complete`, else `Produce`; mid-flight `Review`/`Accept` positions
degrade to a fresh produce). Fail-closed: a transport/HTTP failure throws a
retryable `TammaError`, never a silent "Fresh". It does NOT extend
`TammaApiClient` (whose surface is exactly pinned by the 43-8 mediation sweep;
an internal read does not belong on the effect plane).
`Documents:ReEntryDisabled=true` still swaps in the Null seam. Pinned by
`HttpLifecycleReEntryServiceTests`.

## 2. `HourlyAnalyticsRollupScheduler` is a captive-dependency violation

The singleton hosted service takes the **scoped** Elsa `IWorkflowDispatcher`
(made scoped by Elsa's own registration) in its constructor. Development scope
validation refuses to build the host; Production silently promotes the
dispatcher (captive dependency). The scheduler should resolve the dispatcher
per-tick from an `IServiceScopeFactory` scope (the
`TenantScheduledTriggerService` pattern in the same directory).

*E2E stance:* the fixture runs the engine as Production (matching deployment),
documented at the env-var site in `EngineFullStackFixture`.

## 3. `Output.Set(context, null)` binds to the Variable overload and NREs

`CheckLimitsActivity.RunAsync`'s HAPPY path (`StopReason.Set(context, null)`)
threw `NullReferenceException` inside Elsa's
`OutputExtensions.Set[T](Output<T>, ActivityExecutionContext, Variable<T>)` —
a literal `null` argument binds to the **Variable overload**, not the value
overload, and the extension dereferences the null Variable. Consequence: the
ADL orchestrator could NEVER reach `DispatchCycle` (CheckLimits faulted before
emitting its `Continue` outcome on every pass; `ContinueWithIncidentsStrategy`
recorded an incident and the loop went to cooldown). The engine-driven E2E is
what surfaced it — no prior test drove the orchestrator through a real engine.

**Fixed (2026-08-13):** `CheckLimitsActivity` (empty string per its own
"empty if continuing" contract) and `SelectWorkItemActivity`'s
NothingFound/NeedsTriage paths (typed `(string?)null`).

**Open inventory** — the same literal-null pattern exists at these sites and
will NRE if ever hit; fix by typing the null or setting a real default:
`Review/EscalateReviewActivity.cs:242`, `Review/DeliverGuidanceActivity.cs:107,161`,
`Review/WaitForFixesActivity.cs:125,143`,
`AgentDispatch/DispatchAgentWorkflowActivity.cs:235`.

## 4. `DispatchWorkflowDefinitionRequest` takes a VERSION id — every background dispatch passed the definition id

Elsa 3.5's `DispatchWorkflowDefinitionRequest(string definitionVersionId)`
ctor takes the definition **version** id (`"AdlOrchestratorWorkflow:v1"`), and
every activity/scheduler dispatch site passed the definition id
(`"single-issue-cycle"`), so the dispatch queue handler answered
`WorkflowGraphNotFoundException` for EVERY background dispatch: the
orchestrator could never start a cycle, never restart itself, never dispatch
triage; the analytics rollup and the 41-30 scheduled triggers never fired.
Latent because every one of these dispatches is fire-and-forget-with-swallow
by design. **Fixed:** `Tamma.Activities.Core.PublishedWorkflowDispatch`
resolves the published version id first; all five sites (DispatchCycle,
DispatchAdl, DispatchTriage, HourlyAnalyticsRollupScheduler,
TenantScheduledTriggerService) now route through it.

## 5. `TestingWorkflow.MakeOutput` made the store populator skip HALF-A-DOZEN workflows

`MakeOutput` typed its value parameter `Func<ActivityExecutionContext,
object>`; `Input<object>` has no matching delegate ctor, so `new(value)`
bound the LITERAL ctor and the Func itself became the input's literal value.
Serializing that definition threw `NotSupportedException` inside
`DefaultWorkflowDefinitionStorePopulator.PopulateStoreAsync`, which ABORTS the
whole populate loop — every workflow after TestingWorkflow in enumeration
order (`testing-pipeline`, `triage-context-gathering`, `triage-item-cycle`,
`triage-po-decision`, `update-issue-status`) was silently absent from the
registry, and every `DispatchWorkflow("update-issue-status")` in every cycle
faulted "No published version… found". **Fixed:** the parameter is
`Func<ExpressionExecutionContext, object>` (the delegate-expression ctor).

## 6. Literal-null `Input<T>` defaults are DROPPED by the definition-store round-trip

`EmitCycleEventActivity`'s optional inputs default to `new((string?)null)`;
the JSON round-trip through the workflow-definition store drops a
literal-null expression, the materialized input is null, and `Input.Get`
throws `"<name> is required."` — faulting every CYCLE.STARTED/COMPLETED emit
(the cycle only wires StepId/ErrorDetail on failure emits). **Fixed** with
`GetOrDefault` reads in `EmitCycleEventActivity`; the same trap exists
wherever an optional input defaulted to a literal null is read via `.Get`
after a store round-trip.

## 7. Engine rollup fan-out needs `ITenantDbContextFactory` (open)

`hourly-analytics-rollup`'s fan-out activity resolves
`Tamma.Data.Abstractions.ITenantDbContextFactory`, which no engine
composition registers (same family as item 1) — the rollup workflow now
DISPATCHES correctly (item 4) but faults at fan-out in an engine-only
deployment. Open, same fix direction as item 1.

## 8. A bare `Activity` does NOT auto-complete — 24 emit activities hung their workflow forever

Elsa 3.5: a class deriving from bare `Activity` (not `CodeActivity`) must call
`await context.CompleteActivityAsync()` itself; returning from `ExecuteAsync`
leaves the activity — and therefore the workflow — `Running` forever, with **no
incident, no bookmark, nothing in the journal** (the E2E found
`single-issue-cycle` parked at its FIRST emit node, `EmitCycleStarted`, with
empty bookmarks and zero incidents). `EmitEscalationEventActivity` was the
single emit that carried the correct pattern ("Complete on the default outcome
so a mid-flow emit node continues to the next activity"); the other 24
`Emit*EventActivity` classes ended with `return default;` and would each hang
whichever workflow reached them first. **Fixed (2026-08-13):** all 24 now
`await context.CompleteActivityAsync()` (dated comment at each site). Rule for
new activities: derive from `CodeActivity` (auto-completes), or complete
explicitly.

## 9. `Input.Get` throws "is required." whenever the evaluated value is null — every optional emit input read via `.Get`

Companion to item 6, broader: `Input<T>.Get(context)` throws
`"<name> is required."` for ANY null evaluated value — including an input
**explicitly wired** with a typed null (`new Input<string?>((string?)null)`),
proven by a standalone in-memory probe (no store round-trip involved). So every
OPTIONAL input in every emit activity read via `.Get` was a landmine: the first
workflow to leave one unwired (or wire an expression that evaluates null at
runtime, e.g. `ErrorDetail` on a success emit) faults the emit. **Fixed
(2026-08-13):** the 25 `Emit*EventActivity` classes (the 24 of item 8 +
`EmitEscalationEventActivity`) now read all inputs with `GetOrDefault`; every
call site already tolerated null/default. Also fixed:
`InitAdlConfigActivity` — its five layered-override inputs are all optional,
and the self-restart dispatch (`DispatchAdlActivity`) passes only `configJson`,
so EVERY successor orchestrator instance faulted its own init with
"Repository is required." (the E2E's restart loop surfaced it). Left alone
(out of the loop's path, `.Get` reads still present):
`EmitCleanupTerminalEventActivity`, `EmitDeleteTerminalEventActivity`,
`EmitHourCompletedActivity` — same trap if their optional inputs are ever left
unwired.

## 10. Store-rehydrated activities have NULL ctor-injected members — five HTTP activities silently fell back to `localhost:3100` / mock

A workflow loaded from the definition store is materialized by the JSON
serializer through each activity's `[JsonConstructor]` (parameterless) — so
`IConfiguration` / `IHttpClientFactory` / `ILogger` ctor fields are **null in
the real engine**, and the null `Logger` swallowed every warning that would
have said so. Consequences observed in the E2E: `ResolveConventionsActivity` /
`ResolvePromptFromRegistryActivity` sent every resolve to the
`http://localhost:3100` default (both also DISCARDED a configured
`Engine:CallbackUrl` whenever the factory was null — a second bug in the same
line); `SelectWorkItemActivity` silently used `SimulateCandidates()` instead
of real work selection; `ReadRepoConventionsActivity`,
`StoreFindingsActivity`, `StoreRoleFindingActivity`,
`FetchUntriagedItemsActivity`, `ReportCycleResultActivity` all took their
mock/skip paths. **Fixed (2026-08-13)** with the ctor-or-`context.GetService`
idiom that `TriggerCIActivity`/`UpdateIssueStatusActivity`/the git activities
already used. Still on the old pattern (off the loop's happy path, fail loud
or log-and-skip): `CommitFixActivity` (throws), `TDD/CommitChangesActivity`,
`TDD/RevertRefactoringActivity`. Rule for new activities: never read a
ctor-injected service without the `?? context.GetService<T>()` fallback.

## 11. `GET /api/engine/issues` serves label OBJECTS; both engine consumers parsed `List<string>` — intake silently empty

`PlatformEngineCallbackService.IssueToJson` writes GitHub-shaped
`labels: [{"name":"..."}]` (the pinned wire shape), but
`WorkItem.Labels` declared `List<string>` — so `SelectWorkItemActivity` and
`FetchUntriagedItemsActivity` threw `JsonException` inside a log-and-swallow
catch and the real intake returned ZERO candidates on every tick (the loop ran
"clean repo" forever). The prior regression suite
(`EngineIssuesResponseTests`) green-lit this because its "exact body shape"
sample used plain-string labels — a wrong sample of the very wire it pinned.
**Fixed (2026-08-13):** `LabelNamesConverter` accepts both shapes (objects for
the wire, plain strings for the internal WorkItemJson round-trip),
`WorkItem.Url` maps `html_url`, and the regression suite now pins the REAL
object-shaped body alongside the internal shape.

## 12. Single-user service-plane calls never bind the personal tenant — the autonomy gate 500'd every engine LLM call

The single-user contract everywhere ("single-user binds a personal tenant
up-front; the ambient context carries it" — `LifecycleReEntryService`,
`DocumentInstanceRepository`, `AcceptanceRulesRepository`) was only satisfied
for AUTHENTICATED dashboard requests: `EnsurePersonalTenantMiddleware` bailed
on `IsAuthenticated != true`, and the engine's mediated service calls
(`Tamma:ApiToken` — `POST /api/v1/llm/call`, git callbacks, event drain) carry
no user claim. So every tenant-resident read those calls trigger threw
"requires an ambient tenant id"; the 43-x autonomy gate — CORRECTLY — treated
the failed base-rules read as unreadable and failed CLOSED (AlwaysHuman /
denied), and every engine LLM call answered 500 forever. The gate posture is
right (F6: a blip must not discard the user's always-escalate floor); the bug
was upstream: the READ had no ambient home to succeed against. **Fixed
(2026-08-13):** in single-user mode, an anonymous/service request with no
resolved tenant now resolves the sole user (`ISoleUserProvider` — the same
resolution the gate itself uses) and binds — minting + synchronously
provisioning on first touch, serialized against concurrent service bursts —
their personal tenant; SaaS behavior untouched (mode short-circuit first);
pre-setup deployments (no owner configured, users table empty) pass through
tenant-less as before. Pinned in `EnsurePersonalTenantMiddlewareTests` (9
tests).

## 12b. Same split inside `AgentRegistryService`: claims-only principal ⇒ `AGENT_UNRESOLVED` on every engine call

After the item-12 fix bound the ambient tenant, the llm/call path advanced to
persona resolution and STILL failed: `AgentRegistryService.ResolvePrincipal`
answered the single-user principal from HTTP CLAIMS ONLY, so a service-plane
call resolved `(null, null)` — while the autonomy gate resolved the SAME
request's principal to the sole user via `ISoleUserProvider`. The seeded
default-persona enablement (user-keyed) was invisible to a nobody-principal,
so every engine call failed loud with `AGENT_UNRESOLVED`
(`agent.default_persona.none` despite `agent.enablement.seed_default_applied`
for the same persona seconds earlier). **Fixed (2026-08-13):** single-user
claims-less resolution falls back to the same `ISoleUserProvider` seam the
gate uses (cached after first success; pre-setup deployments unchanged).
Rule: every single-user principal resolution must go through the sole-user
seam, not HTTP claims — a service call has no claims.

## 13. The singleton `IProviderCredentialResolver` captured the scoped `IEventRepository` — first llm/call 500s on a Development host

`AddProviderCredentialResolution` registers the resolver as a SINGLETON (the
BYOK-cache contract) whose factory resolved the SCOPED `IEventRepository` from
the singleton's root provider — a captive dependency. Production silently
promotes it (one DbContext-backed repository living forever inside the
singleton); a Development host (`ValidateScopes`) throws "Cannot resolve
scoped service ... from root provider" on the FIRST credential resolution —
500ing every `POST /api/v1/llm/call`. Same family as item 2
(`HourlyAnalyticsRollupScheduler`), API-side this time. **Fixed
(2026-08-13):** the resolver keeps its singleton lifetime and audits through a
scope-per-append `ScopePerCallEventRepository` adapter
(`IServiceScopeFactory`), the `TenantScheduledTriggerService` pattern.

## 14. Workflow VARIABLES are EPHEMERAL across suspend/resume unless a storage driver is set

`builder.WithVariable(...)` declares a variable with NO storage driver, so its
value lives only in the in-memory workflow state — the FIRST timer/event
bookmark suspension serializes the instance and every undriven variable comes
back EMPTY ("empty state json" post-resume). All ~1000 `WithVariable`
declarations across the 49 CLR workflows now chain `.Persisted()`
(`VariablePersistenceExtensions` — sets
`StorageDriverType = typeof(WorkflowInstanceStorageDriver)`), so values survive
suspend/resume. Pinned by the workflow structure tests.

## 15. A long in-activity `Task.Delay` holds the Elsa dispatch worker slot — the cooldown deadlocked the whole loop

`CooldownActivity` slept `Task.Delay(cooldownSeconds)` **inside** the activity,
which occupies the runtime's dispatch slot for the whole cooldown: with the E2E
config (3600 s) every subsequently dispatched workflow — all of the cycle's
llm-calls included — queued behind the sleeping orchestrator and the loop
deadlocked itself. The activity now only EMITS the audit pair; the actual wait
is a stock `Elsa.Scheduling.Activities.Delay` node ("CooldownWait", a timer
bookmark that SUSPENDS the instance) sequenced right after it in
`AdlOrchestratorWorkflow`. Pinned by `AdlLoopDurabilityTests`.

## 16. Single-user decision gates suspend TENANTLESS; the resume endpoint looked up only the tenant-scoped bookmark name

`WaitForDocumentDecisionActivity` folds the tenant into the bookmark name. A
single-user cycle dispatches its gates with an EMPTY tenant, but the API's
service-plane binding hands every caller the sole user's PERSONAL tenant — so
the resume seam computed `document-decision-{tenant}-{session}` while the gate
was suspended on `document-decision--{session}`: 404, "no decision waiting",
while the gate waited forever (the deployed single-user dashboard accept has
the same bug). `DocumentDecisionResumeEndpoint` now falls back to the
tenantless name for the SAME session when the tenant-scoped lookup misses —
one principal, two spellings. SaaS gates always carry their tenant, so the
fallback matches nothing cross-tenant.

## 17. The API's rate limiter throttled the autonomous loop it hosts (429 storm)

The fixed `ConfigRead`/`ProviderExecute` limits were sized for a human
dashboard, not for a 7-role panel fanning out through the engine: one cycle
produced 1452 rejected requests (each engine retry re-amplifying ×4). The four
policies now read their limits from configuration
(`RateLimits:ConfigRead` etc., defaults unchanged), and the E2E fixture raises
them — a deployed autonomous instance must do the same or it throttles itself.

## 18. `POST /api/prompts/render` 500'd on numeric/boolean variables

The render DTO declared `Dictionary<string, string>` — the engine's variable
payloads carry numbers and booleans (`issueNumber`, `reEntry`), which
System.Text.Json refuses to bind to `string` (500 before the handler).
The DTO now takes `Dictionary<string, JsonElement>` and stringifies per kind
(String → GetString, everything else → GetRawText). Pinned by the prompt
endpoint tests.

## 19. Reviewer replies have TWO consumers with TWO shapes — declaring `documentType` routes them through the 39-9 ring

`SingleReviewerWorkflow` (39-7 panel path) declares `documentType="review"`,
so the API's 39-9 content-validation ring validates the reply against the
REVIEW registry validator — the legacy verdict JSON (`{"verdict":"approve"}`)
does not satisfy it, and every panel member exhausted the repair ring (48×
`DOCUMENT.VALIDATED.FAILED` in one run). The cycle's own
plan-review/task-review parse the LEGACY verdict directly (no documentType).
The scripted provider therefore serves BOTH shapes: qualified
`{role}/{action}@review` cells answer the canonical approving `Review`;
bare `{role}/{action}` cells answer the legacy verdict. Pinned by
`ScriptedCycleScriptValidityTests`.

## 20. `InitAdlConfigActivity` silently ignored camelCase `configJson`

The orchestrator's `configJson` input deserialized with default (exact-case)
options, so `{"cooldownSeconds": 3600}` parsed to the DEFAULT 10 s cooldown —
18 cycles stormed in one E2E window before the loop was reined in. Now
`PropertyNameCaseInsensitive = true`.

## 21. `ReportCycleResultActivity` faulted on its OPTIONAL `Error` input ("Error is required.")

Item 9's family, one more member: the activity read `Error.Get(context)` — on
every exit path with no error (success, needsHuman, deferred) the evaluated
value is null and `.Get` THROWS, so the report activity faulted after doing
its work. Now `GetOrDefault`.

## 22. ASP.NET route values keep `%2F` ENCODED — the issue-id document routes silently matched nothing

Issue ids are `{owner}/{repo}#{n}`, so every correct caller of
`GET /api/documents/issues/{issueId}/latest` (and `/lineage`) escapes the id
into one path segment (`tamma-bot%2Ftest-repo%231`). ASP.NET Core decodes
route values EXCEPT the encoded slash (anti-path-splitting), so the handler
received `tamma-bot%2Ftest-repo#1` — which matched no store row and returned a
perfectly healthy-looking **empty 200**. Effect: the engine's latest-accepted
read (item 1's HTTP seam) found nothing, the plan-review shim escalated a plan
the lifecycle had JUST accepted, and the cycle died needs-human with zero
warnings anywhere (E2E run 30). The handlers now fold the leftover `%2F`
(`FoldEncodedSlashes` — deliberately NOT a full second unescape, which would
double-decode literal percent escapes).

## 23. Two mediation paths chose DIFFERENT LLM providers for the same role

The `llm-call` workflow resolves its provider chain (caller > DB agent config >
`Llm:DefaultProviderChain`) and passes the chosen provider EXPLICITLY on each
`/api/v1/llm/call`; the engine's direct single-shot path (`MediatedLlmText` —
every TDD/debug/refactor activity) passed NO provider, so the API's
`ManagedAgent` fell back to the resolved persona's provider. A deployment whose
selected chain holds no key for the persona default (the E2E: chain=scripted,
persona=claude→anthropic, no anthropic key) fails ONLY on the second path —
run 33 died `PROVIDER_CREDENTIAL_UNAVAILABLE` at the first TDD write-tests
after 29 successful scripted calls. `MediatedLlmText` now applies the same
deployment-tier selection (first allowlist-passing entry of
`Llm:DefaultProviderChain`; null → persona default as before).

## 24. Dispatch-result numbers rehydrate as `long`/`JsonElement` — `is int` silently loses a REAL PR number

A child workflow's `SetOutput` int crosses the parent's suspension as a JSON
number, which Elsa's object rehydration infers as `long` (or leaves as
`JsonElement`) — never `int`. Booleans survive as CLR bools, which is why the
sibling gates' `s is true` checks passed while the cycle's
`n is int num` PR-number extraction was silently false for a PR that EXISTS:
run 32 failed `PrOk` 200 ms after `GIT.PR_OPENED.SUCCESS`.
`SingleIssueCycleWorkflow.CoerceInt` (int/long/double/decimal/string/
JsonElement) now backs the extraction; `DebuggingWorkflow` had already learned
the int/long half of this lesson independently (line ~1225).

## 25. `testing-pipeline` never captured its callers' inputs — the CI wait suspended with an empty repository and the DG-5 listing HID it

Every dispatcher of `testing-pipeline` (TddWorkflow, CiWithDebugRetry,
Debugging, Mentorship) passes `SessionId`/`Repository`/`Branch`/`SkillLevel` —
and nothing in `TestingWorkflow` ever read them (declaring a same-named
VARIABLE does not bind a workflow INPUT). The variables kept their defaults,
`WaitForCIResultsActivity` suspended with `repository=""`, and the DG-5
`/elsa/api/ci/waits` listing — which fail-closed skips blank-repository
payloads — silently HID the wait: no CI seat could ever resume it, and every
TDD leg could only exit via the timeout. The init step now captures all four
inputs (run 36). Same family as item 20 (an input nobody reads is not an
error anywhere).

## 26. The TDD test-syntax validator runs `tsc --noEmit` on jest-style `.ts` test files

`ValidateTestSyntaxActivity` type-checks generated `.ts`/`.tsx` test files
with a bare `tsc --noEmit` — jest globals (`describe`/`it`/`expect`) fail
type resolution outside a project with test typings, so ANY jest-style
TypeScript test the LLM writes is rejected as a syntax error when `tsc`
happens to be on PATH (run 35: three debug-retry attempts, all
`test-syntax-invalid`, cycle error) and silently skipped when it is not —
environment-dependent behavior. The scripted cycle sidesteps it with `.js`
test files (no validator wired for JavaScript — deterministic skip); the
real fix (a jest-aware tsconfig for the dry-run, or skipping known-framework
globals) is recorded here, not attempted.

## 27. `ExecuteAgentActivity` failed EVERY execution in a real engine (item-10 family) — and the loop silently degraded

The task loop's primary path is agent execution (`TddForTask`,
`ExecuteAgentActivity` → `AgentExecutorFactory` → Local/GitHubActions
executor). Store-rehydrated instances carry a NULL ctor-injected factory, so
every execution failed instantly ("AgentExecutorFactory not registered", 2 ms)
and the loop fell through to its debug-retry leg — which "worked", masking the
fact that the REAL implementer (the agent that commits actual code to the
branch) never ran once. Fixed with the ctor-or-GetService idiom (run 38).

## 28. The TDD fallback leg's `CommitChangesActivity` fabricates commit SHAs — `COMMIT.CREATED.SUCCESS` for commits that never happened

Two independent lies compose on the fallback path:
(a) the rehydrated activity's `_configuration` is null, so
`_configuration?["Engine:CallbackUrl"]` reads null and the code silently takes
its `SimulateCommit` branch — emitting `COMMIT.CREATED.SUCCESS` with a
**fabricated SHA** onto the audit stream (run 38: 2 "successful commits",
`head == base`, an EMPTY PR that Gitea refused to merge);
(b) even with DI intact, the "real" path POSTs `action=git_commit` to
`POST /api/engine/execute-task` — which is an LLM-proxy bridge that does not
implement git operations at all. The activity cannot create a real commit by
EITHER path. OPEN: the fallback TDD leg needs a real commit seam (the
mediated contents API, or dispatching the agent executor); until then its
green terminal must not be read as "code landed". The E2E's real-commit proof
rides the agent-executor path (item 27), not this one.

## 29. The self-merge race: the merged-PR webhook fires BEFORE `WaitForPRMerged` registers — the once-only delivery was lost

When Tamma performs the merge ITSELF (the merge-approval gate's happy path),
the platform delivers `pull_request closed(merged=true)` immediately —
observed **1 second before** the cycle transitioned into
`WaitForPRMergedActivity` and registered its bookmark (run 39: forward → 404
"no suspended bookmark" at 22:54:46; bookmark registered 22:54:47.6). The
delivery is once-only, so every SELF-merged cycle — the autonomous loop's
normal case, every platform — could only finish through the 12 h SLA
needs-human handoff. **Fixed (reconcile-on-register):**
`PrMergedResumeEndpoint` buffers a bookmark-miss delivery in the singleton
`PendingPrMergeBuffer` keyed by the qualified bookmark name (202, no longer a
plain 404) and `WaitForPRMergedActivity` consumes it at registration,
completing `Merged` without suspending. In-memory on purpose — the buffer
bridges a seconds-wide in-process ordering race; an engine restart inside the
window still lands on the wait's durable 12 h SLA edge. Cross-tenant safety
holds (the buffer key IS the caller's tenant-qualified name). Pinned by
`PrMergedResumeEndpointTests`.

## 30. Gitea answers the merge endpoint "405: Please try again later" while its ASYNC mergeability check runs — the driver treated it as terminal

After any head push or PR edit (the cycle un-drafts via a title PATCH seconds
before merging), Gitea recomputes mergeability asynchronously and the merge
endpoint answers 405 "Please try again later" until it settles. The driver
treated that as a terminal `NOT_MERGEABLE` and the merge-approval gate
escalated a perfectly mergeable squash to a human (runs 37–38).
`GiteaPlatformClient.MergePullRequestAsync` now retries exactly that answer
(bounded, backoff); every other failure stays terminal. Pinned by
`GiteaMergeabilityRetryClassifierTests`. (Run 38 also showed the retry alone
is insufficient when the PR is EMPTY — see item 28 — which is why both fixes
were needed.)

## 31. OPEN — Elsa dispatch-queue serialization race can LOSE a queued dispatch ("Collection was modified")

Observed once (run 40, ~1-in-15 frequency): the engine's workflow dispatch
queue processor crashed serializing a queued item
(`PolymorphicDictionaryConverter.Write` →
`InvalidOperationException: Collection was modified; enumeration operation may
not execute` → "An unhandled exception occurred while processing the queue")
while the dispatching parent was still executing (its fire-and-forget notify
dispatches run concurrently with the queue's serialization of the same
in-flight state). The queued `pull-request` dispatch was consumed by the crash
and never ran; later items processed fine; the parent cycle suspended forever
on `WaitForCompletion`. This is an Elsa 3.5 runtime-level race (their
serializer enumerating a dictionary the executing workflow still mutates) —
not fixed here. Mitigation direction: serialize dispatch inputs eagerly
(deep-copy at enqueue), or upstream fix. A cycle that stalls with a dispatched
child that never appears in the instance list + this queue crash in the log is
THIS defect.

## 32. The provider allowlist's config extension point was DEAD on the main LLM path — FIXED

`Security:ProviderAllowlist:AdditionalProviders` is the documented way to
admit a self-hosted or custom provider, and `ProviderAllowlist` binds it
correctly — but `InlineToolLoopRunner.LoadProviderConfig` validated against
`ProviderAllowlist.IsAllowedDefault`, a static convenience built from a
`DefaultInstance` that is constructed with NO options. So a configured
provider passed selection (chain resolution, agent config) and was then
rejected at the last moment with "Provider 'x' is not in the allowlist,
rejecting", after which the caller silently fell back to the platform default
— in the E2E, a real vendor with no credentials, so every LLM call answered
200 with empty content and the loop produced nothing. No deployment could ever
have used the extension point on this path. **Fixed (2026-08-14):** the runner
binds `Security:ProviderAllowlist` from its injected `IConfiguration` (cached
per runner) and consults that instance; the built-in defaults still apply when
nothing is configured. The E2E fixture now sets the entry as well — enablement,
chain selection AND allowlisting are all required for a provider to serve a
call, and it had only the first two.

## 33. Nine MORE `Output.Set(context, null)` NRE sites (item 3's class was not fully swept) — FIXED

Item 3 fixed the sites the first pass found; nine survived and each NRE'd at
runtime the moment its activity ran: `UpdateIssueStatusActivity` (×2 — this one
faulted mid-cycle in run 47), `WaitForPRMergedActivity`, `EscalateReviewActivity`,
`DeliverGuidanceActivity` (×2), `WaitForFixesActivity` (×2),
`DispatchAgentWorkflowActivity`. The compiler flags every instance as CS8625
("cannot convert null literal to non-nullable reference type") because the
literal binds the `Variable<T>` overload, so the warning list IS the detector.
**Fixed (2026-08-14):** all nine pass a typed null; `grep -rn "\.Set(context,
null)" src/` now returns zero.

## 34. Background ticks are TENANT-LESS, so every background actor in a single-user deployment is gated off forever — FIXED

A background tick has no HTTP scope, so `EnsurePersonalTenantMiddleware` (the
item-12 fix) never runs and the ambient tenant is null. Every tenant-resident
read the autonomy gate performs then throws ("AcceptanceRulesRepository
requires an ambient tenant id"), the gate CORRECTLY reads unreadable policy as
fail-closed, and `Apply` skips the tick — permanently, for every actor
(`outbox-smtp-sender`, `task-queue-processor`, ...), at one ERROR log per tick
(818 in a single 14-minute E2E run). This is a production defect in any
single-user deployment, not a test artifact. **Fixed (2026-08-14):**
`BackgroundActionGate` binds the sole user's EXISTING personal tenant onto the
tick's scope before evaluating (`ISoleUserProvider` + membership lookup — the
same seam the middleware uses). Read-only on purpose: minting a tenant belongs
to a user request, so pre-setup deployments behave exactly as before, and the
gate's fail-closed posture is untouched.

## 35. Harness: one 5s docker probe failed 23 tests on a cold CI runner — FIXED

`DockerAvailability` probed `docker info` exactly once with a 5s budget. A
cold GitHub runner exceeded it, `RequireOrSkip` threw under
`PLATFORMS_REQUIRE_DOCKER=true`, and all 23 Gitea+Forgejo tests failed
OneTimeSetUp on an infrastructure hiccup (PR #512, 2026-08-14). **Fixed:**
where docker is required (CI) the probe retries within a 60s budget; on a
laptop it keeps the single fast probe so test discovery never stalls.
