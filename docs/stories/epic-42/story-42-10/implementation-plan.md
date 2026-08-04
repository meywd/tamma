# Implementation Plan — Story 42-10: Shell Sandbox Profile and `secret.read` Enforcement

Written 2026-08-03 against the working tree. Every file:line below was re-verified on that date;
where the story's citation and the tree disagree, the tree wins and the difference is recorded in
**Corrections to the story**.

## Scope & Deliverable

When this story is done:

1. A shell tool call never sees the API process's secrets — `psi.EnvironmentVariables` is an
   explicit allowlist, in **both** profiles, always. The `env` leg of `secret.read` is closed by
   construction.
2. `Tools:Shell:Sandboxed=true` is a declared, startup-verified profile: egress blocked (attested
   per mechanism, probed where probeable) and CWD confined. A deployment that claims the profile
   without the guarantees refuses to start.
3. The shipped `DefaultMinAutonomy` for `tool:shell_execute` and `effect:process.spawn` is a
   catalog-build input: **40 sandboxed / 80 not** (levels per the 43-11 zone model; the dial
   governs the LLM only — humans and deterministic machinery are never gated).
4. `effect:secret.read` exists at 90 (manage-secrets zone), enforceable, caller-kind LLM. It is
   enforced for real at the reveal route (Seam C, LLM callers only) and best-effort in the tool
   loop (Seam B shell grading). `effect:secret.reveal` keeps its catalog row in the machinery
   inventory, its audit row, and its token expiry — off the dial.
5. `agent-action:audit-secrets` is pinned metadata-only: a test fails the moment the audit path
   can return a secret **value**.

## Pre-Reading

All paths under `apps/tamma-elsa/` unless noted. Verified 2026-08-03.

| File:line | Why |
|---|---|
| `docs/stories/epic-42/story-42-10/42-10-shell-sandbox-profile-and-secret-read-enforcement.md` | The ACs — source of truth. |
| `docs/stories/epic-43/story-43-11/43-11-automation-level-model-and-per-action-levels.md` — Amendments 2 (§D), 3, 4; the caller-kind re-audit; the Dial-governed and Machinery tables | The ruling model: zones at 5-point steps; dial governs the LLM only; `secret.read` minted at 90 (`:1290`, `:1460-1481`); `secret.reveal` retired to machinery (`:1336`); shell 80 unsandboxed / ~40 sandboxed (`:747-756`, `:1270-1271`). |
| `src/Tamma.Activities/LlmCall/Tools/ShellExecuteTool.cs:86-94` | THE verified hole: `ProcessStartInfo` sets FileName/WorkingDirectory/redirects and **no `EnvironmentVariables`** — the child inherits the API's whole environment. Ctor config reads at `:39-47` (`ToolExecution:WorkspaceRoot`, `ToolExecution:ShellTimeoutSeconds`); denylist screen at `:64-76`; `process.Start()` at `:120`. |
| `src/Tamma.Activities/LlmCall/Tools/RunTestsTool.cs:116-124` | The **same** `ProcessStartInfo` shape, same env leak, same `/bin/bash -c`. See D1. |
| `src/Tamma.Activities/LlmCall/Tools/CommandValidator.cs:16-59` | The blocked-pattern denylist — **16 entries, not the story's 18** (see Corrections). `ShellMetacharacters` at `:66-68`. The precedent for a shared command screen. |
| `src/Tamma.Activities/LlmCall/Tools/PathValidator.cs:7,18` | `ResolveSafePath(path, workspaceRoot)` — existing traversal/symlink-safe workspace resolution; reused by the CWD-confinement screen's path legs. |
| `src/Tamma.Api/Endpoints/SecretEndpoints.cs:176-210` | `RevealSecret` — the reveal exchange handler; the enforcement target for AC5. |
| `src/Tamma.Api/Program.cs:2597-2603` | The reveal route registration: `MapGet("/api/v1/secrets/reveal/{token}")`, `.RequireRateLimiting("SecretReveal")`, **no authorization policy** — "the token IS the auth". This anonymity is why AC5 needs 43-13's caller-kind, and why the anonymous case must fail closed to LLM (D6). |
| `src/Tamma.Api/Program.cs:735-746` | Tool-executor DI registrations (`ShellExecuteTool` at `:741`). |
| `src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:332-391` | Seam B as built: gate call `_autonomyGate.Evaluate(tc.ToolName, tc.ArgumentsJson)` at `:353`; denial joins `rejectedToolCalls` at `:381`; required ctor param `:73`, null-throw `:94`. **This story does not edit the seam** — grading happens inside the gate's name-resolution (D7). |
| `src/Tamma.Api/Services/Agents/CatalogDefaultToolLoopAutonomyGate.cs:241-265` | `TryResolveKey` — the one argument-bound split today (`git_operations` by subcommand, `:259-262`). The shell secret-read grading is a second split at exactly this seam. DI factory: `src/Tamma.Api/Extensions/ActionCatalogGovernanceServiceCollectionExtensions.cs:112-115`. |
| `src/Tamma.Api/Services/Actions/ToolNameAliases.cs:33-56,125-137` | The name→key map (`Bash`→`shell_execute` at `:48`) and `TryResolveGit` — the pattern the shell split copies. |
| `src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs:49-53` | The `Tool(...)` helper **hardcodes `AutonomyDial.Min`** — no level parameter. 43-11 gives all six helpers a level; step 5 here makes the two shell rows profile-dependent. `ShellExecute` descriptor `:272`; `ProcessSpawn` `:396-397` (SiteKey literally "…ShellExecuteTool → ProcessStartInfo" — same executor, confirming the twin treatment); `SecretReveal` `:393-395` (`enforceable: false`, SiteKey "GET /api/v1/secrets/reveal/{token} — SecretEndpoints.RevealSecret" — the new `secret.read` SiteKey must differ or `DUPLICATE_SITE_KEY` boots the app). |
| `src/Tamma.Core/Actions/ActionCatalog.cs:54-56,150-228` | Static catalog: `s_descriptors = BuildDescriptors()` at class init; `BuildIndex` **refuses any `DefaultMinAutonomy` outside `[Min,Max]∪{AlwaysHuman}` at `:181-184`** — with `Min=70` today, a 40 is a boot failure. `Validate()` at `:86` builds directly (the test seam D4 extends). |
| `src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27,30,38,48-49` | `Min=70`, `Max=100`, `AlwaysHuman=101`, `IsValidThreshold`. 43-11 edits `Min` to 1 — step 5's hard prerequisite. |
| `src/Tamma.Core/Actions/ExternalEffect.cs:111,120,126` | The enum: `McpToolInvoke`, `SecretReveal` (`[Wire("secret.reveal")]`), `ProcessSpawn`. `SecretRead` is inserted adjacent to `SecretReveal` (wire pin is order-sensitive). |
| `src/Tamma.Api/Services/Actions/ActionCatalogStartupValidator.cs:31-70` | The fail-loud hosted-validator pattern (aggregate violations, one `TammaError`, refuse boot) that `ShellSandboxStartupValidator` copies. Also gains the profile-consistency check (D4). |
| `src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:91,101-108,198-201` | Seam C: `.EnforcesGovernance()` opt-in, 409 `ACTION.GATE.REQUIRES_HUMAN` body with an actionable `authorizationId`. `.Governs(ActionKey)` is `GovernsExtensions.cs:39`. |
| `src/Tamma.Api/Services/Secrets/Query/ISecretQueryService.cs:18-20` + `SecretQueryService.cs:14-16,60,118,186` | The audit-metadata read path: "Plaintext is never returned by any method on this interface" is currently **prose**; the entity graph it queries carries `Ciphertext` (nulled on retire at `:186`), so the guarantee lives in the projections (`ProjectMetadata`/`ProjectVersion`). AC7 turns the prose into a pin. |
| `src/Tamma.Api/Services/Secrets/` (dir) | Story 29-1 landed: `ISecretStore`, `SecretRef`, `SecretScope`, `SecretPurpose`, `ISecretAccessAuditor`, Postgres backend. The story's dependency is satisfied. |
| `tests/Tamma.Core.Tests/Actions/ActionVocabularyCountTests.cs:53-81,132-149` | `ExternalEffect_has_39_members` and `TotalCatalogMembers_is_197` — the two headline pins, each with the history-comment convention this story's +1 must follow. |
| `tests/Tamma.Core.Tests/Actions/ActionWirePinTests.cs:40-66` | `ExternalEffect_wires_are_pinned` — ordered 39-string list; gains `"secret.read"` in enum order. |
| `tests/Tamma.Core.Tests/Actions/ActionGroupMembershipTests.cs:219-227,288` | `Secrets_has_the_4_expected_members` + the count table (`[ActionGroup.Secrets] = 4`) — both go 4→5. |
| `tests/Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.cs:83-91` | `EveryOtherMember_DefaultsToMin` — goes red the moment `secret.read` ships at 90. Owned by 43-11's rewrite; the pre-43-11 fallback is a named carve-out (step 4). |
| `tests/Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:111-215,543,676-690` | `EffectPerformingSites` — **total over the enum**; a new member with no entry is red. `SecretReveal` is `RouteOnly` at `:151`, `ProcessSpawn` `InProcess` at `:205` — `SecretRead` gets a `RouteOnly` entry. No new client method ⇒ `KnownNonEffectClientMethods` (19, `:800`) and the discovered-surface pin (37, `:664`) do **not** move. |
| `tests/Tamma.Api.Tests/Actions/ActionEnforcementSitesTests.cs:167-169,195-200` | `withSites == 21` and `bound.Count == 21` — both go 22 when the reveal route binds. The partition arithmetic at `:195-198` (bound + baselined == `PinnedInScopeCount`) balances because the baseline entry is deleted in the same commit. |
| `tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:221,235,250,428-429` | `PinnedCount = 216`, `PinHistory = [237, 216]`, `PinnedInScopeCount = 239`; the reveal route's baseline entry at `:428-429` (justified as "…deliberately NEVER enforceable") is **deleted** — the justification becomes false the moment the route enforces `secret.read`. |
| `tests/Tamma.Api.Tests/Actions/GovernedEndpointEnforcementSweepTests.cs:70-93,137-139` | `EnforcementOptedInRoutes` — the D15 written-down opt-in list (16 routes, exact-set both directions, `HaveCount(16)`). The reveal route is line 17. |
| `tests/Tamma.Activities.Tests/LlmCall/Tools/ShellExecuteToolTests.cs`, `RunTestsToolTests.cs`, `CommandValidatorTests.cs`, `PathValidatorTests.cs` | Existing fixtures the tool-level tests extend. |
| `docs/stories/epic-43/story-43-13/implementation-plan.md` (esp. `:403-405` and the Seam C caller table) | 43-13 owns `CallerKind`/`CallerKindResolver` and the machinery fixture; **its plan already records that 42-10's AC5/AC8 are blocked on it.** Seam C rule: user principal → Human; service/installation/anonymous → **Llm, fail-closed**. |
| `docs/stories/epic-43/story-43-14/implementation-plan.md:411-413` | 43-14's step 10 edits the same Seam B region — "sequence one after the other (either order); do not run the lanes concurrently." |
| `docs/stories/epic-43/story-43-12/implementation-plan.md:409-416` | The pin-file contention rule: 42-10/43-12/31-13 share `ExternalEffect.cs`, the wire/count pins, the mediation-sweep map, the descriptors — "serialize the pin-file commits; whichever lands second rebases its counts." |

## Corrections to the story

1. **"18-pattern denylist" is 16.** `CommandValidator.BlockedPatterns` (`:16-59`) has 16 entries
   (count them: rm_rf_root … backtick_substitution). Nothing turns on the number; the plan and
   tests say 16.
2. **The story's `SecretEndpoints.cs:176` / `ShellExecuteTool.cs:86-94` / `CommandValidator.cs:16-59` /
   `ActionVocabularyCountTests.cs:132-149` citations are all still exact** — re-verified; no
   drift since drafting.
3. **AC5's "human caller is untouched" needs one honest qualification.** The reveal route is
   deliberately anonymous (`Program.cs:2597-2603` — the token is the auth). Under 43-13's
   fail-closed Seam C rule, *anonymous* grades as **Llm**. A human who exchanges the reveal URL
   from an authenticated browser session (the dashboard sends credentials) is Human and untouched;
   a human who pastes the URL into bare `curl` is indistinguishable from the tool-loop bypass and
   gets the 409 below dial 90. That is the correct security posture — the anonymous leg IS the
   LLM leg — but it is a UX change for one edge and is recorded here and in the AC5 test comments
   rather than discovered in production. (See D6; not a blocker.)

## Design Decisions

- **D1 — The env allowlist is unconditional, lives in one shared helper, and covers both
  spawn sites.** New `ProcessEnvironmentAllowlist.Apply(psi, configuration)` in
  `Tamma.Activities/LlmCall/Tools/`: clear `psi.EnvironmentVariables`, repopulate from the base
  allowlist (`PATH`, `HOME`, `TMPDIR`, `TERM`, `USER`, `LANG` + `LC_*`) plus the additive
  configured list `Tools:Shell:EnvAllowlist` (names only, never values). Applied in
  `ShellExecuteTool` **and** `RunTestsTool` — `RunTestsTool.cs:116-124` is the identical
  `ProcessStartInfo`/`/bin/bash -c` shape, the story's P0 rationale ("any shell tool call can
  read the deployment's credentials") applies to it verbatim, and `effect:process.spawn`'s
  SiteKey names the executor pattern both share. *Rejected:* profile-gating the strip (the story
  is explicit: inheriting the API's secrets is never correct); output redaction instead of
  stripping (the value still reached the child and can exfiltrate without touching stdout);
  shell-only scope (leaves the same leak one file away). *Risk accepted:* stripping can break
  toolchains needing e.g. `DOTNET_ROOT`/`NUGET_PACKAGES` — that is what the additive config list
  is for, and the risk section says so.

- **D2 — New config lives under `Tools:Shell:*`, exactly as the ACs spell it; the existing
  `ToolExecution:*` keys are untouched.** AC2 names `Tools:Shell:Sandboxed` verbatim; renaming
  the existing `ToolExecution:WorkspaceRoot`/`ShellTimeoutSeconds` would be unrelated churn.
  New keys: `Tools:Shell:Sandboxed` (bool, default false), `Tools:Shell:EnvAllowlist` (string
  list), `Tools:Shell:Egress:Mechanism` (`network-namespace` | `proxy-only` | `firewall`),
  `Tools:Shell:Egress:ProbeHost` (optional host:port the startup probe must FAIL to reach),
  `Tools:Shell:SecretPaths` (string list, D7). *Rejected:* folding into `ToolExecution:*` —
  contradicts the AC text for no gain.

- **D3 — Sandbox verification is a fail-loud startup validator, not a per-call check.** New
  `ShellSandboxStartupValidator : IHostedService` (Tamma.Api), pattern copied from
  `ActionCatalogStartupValidator.cs:31-70` (collect all violations, throw one `TammaError`
  naming every offender). With `Tools:Shell:Sandboxed=true` it verifies: (a) egress — the
  mechanism attestation is present and well-formed, and when `ProbeHost` is configured an
  outbound TCP connect **must fail** within a short timeout (a probe that connects means egress
  is open: refuse to start); (b) CWD confinement — `ToolExecution:WorkspaceRoot` is set,
  absolute, and exists (the runtime screen in step 2 is keyed on the same profile flag, so
  "confinement in force" is a wiring fact the validator can assert). With `Sandboxed=false` the
  validator is a no-op. *Rejected:* verifying lazily at first tool call (a mis-sandboxed
  deployment would run at level 40 until then — the level is earned by the guarantee, so the
  guarantee must precede traffic); re-probing per call (cost, and no new guarantee over
  boot-time verification plus the runtime screen).

- **D4 — The profile-dependent shipped level is a catalog-build input with a startup
  consistency check, because the resolver ladder cannot express it.** The ladder composes by
  `max()` (`AutonomyGateEvaluator`), so a platform assignment row can never LOWER the shipped 80
  to 40 — the story says this, and the tree confirms it. Mechanism: new static
  `ShellExecutionProfile` (Tamma.Core/Actions) holding `Sandboxed` (default false) with a
  set-once `Initialize(bool)`; both hosts call it from Program.cs composition **before any
  catalog touch**; `BuildDescriptors()` reads `ShellExecutionProfile.ShippedMinAutonomy`
  (40/80) for the two rows, and an internal `BuildDescriptors(bool shellSandboxed)` overload is
  the test seam (mirroring `Validate()`'s existing direct-build at `ActionCatalog.cs:86`).
  Because static init order is a real hazard, `ActionCatalogStartupValidator` gains one check:
  `ActionCatalog.Get(tool:shell_execute).DefaultMinAutonomy` must equal the value the host's
  configuration implies — a catalog frozen before `Initialize` ran refuses to boot instead of
  silently shipping the wrong level. *Rejected:* a runtime branch in the gates (the catalog is
  what the admin UI and the drift harnesses render — a branch makes the catalog lie);
  a startup-written assignment row (max() cannot lower — see above); making `ActionCatalog`
  non-static/DI-resolved (touches every consumer, out of scope).

- **D5 — `effect:secret.read` descriptor shape.** `ExternalEffect.SecretRead`,
  `[Wire("secret.read")]`, declared adjacent to `SecretReveal` (the wire pin asserts enum
  order). Descriptor: group `Secrets`, risk `ReadOnly` (honest — it reads), `reversible: false`
  (a value in a model transcript cannot be un-read; the level prices exactly that),
  `enforceable: true`, `EscalatableToHuman: true`, level **90** explicit, SiteKey
  `"GET /api/v1/secrets/reveal/{token} — LLM-caller value read into model context (42-10)"` —
  textually distinct from `secret.reveal`'s SiteKey (`Descriptors.cs:394`) or the
  `DUPLICATE_SITE_KEY` boot check (`ActionCatalog.cs:194-198`) fires. Caller-kind LLM per the
  43-11 re-audit (`:1290`).

- **D6 — The reveal-route gate is Seam C's standard opt-in plus 43-13's caller split; anonymous
  fails closed to LLM.** The route gains `.Governs(effect:secret.read)` +
  `.EnforcesGovernance()` (`GovernsExtensions.cs:39`, `GovernanceEnforcement.cs:101-108`) and a
  line in `EnforcementOptedInRoutes`. 43-13's resolver grades: user principal → Human (filter
  automates — the dial never gates a person); service/installation/anonymous → Llm. On this
  route "anonymous" is precisely how the shell-curl bypass arrives (the tool-loop child holds no
  credential once D1 lands — it can still `curl` the route it finds in a transcript), so
  fail-closed-to-Llm is what makes the gate mean something. Denial is the standard 409 with the
  pending-authorization id (`GovernanceEnforcement.cs:198-201`); a 43-14 correlation-standing
  grant covers it like any 90-zone action (semantics inherited, no code here). No engine-side
  caller exists (`TammaApiClient` has no reveal method — verified), so nothing wedges.
  *Rejected:* enforcing only for affirmative engine principals (misses the anonymous curl leg,
  which is the main LLM path); a second LLM-only route (two routes to one secret, and the
  bypass would just use the old one).

- **D7 — Shell secret-read grading is a resolution-time reclassification inside the Seam B
  gate, denylist-strength, and says so.** Same mechanism as the git split
  (`CatalogDefaultToolLoopAutonomyGate.TryResolveKey:257-262`): when the name resolves to
  `shell_execute` (incl. the `Bash` alias), a new
  `ShellSecretReadScreen.Matches(command, secretPaths)` (Tamma.Api/Services/Actions) grades the
  call to `effect:secret.read` instead of `tool:shell_execute`. Shapes: `\benv\b`,
  `\bprintenv\b`, `export -p` / `declare -x`, and a read verb
  (`cat|less|more|head|tail|grep|cut|awk|sed|sort|xxd|base64|od|strings`) whose arguments touch a
  configured secret path (`Tools:Shell:SecretPaths`; defaults `.env`, `.env.*`, `/run/secrets`,
  `*.pem`, `*.key`). Options reach the gate through its DI factory
  (`ActionCatalogGovernanceServiceCollectionExtensions.cs:112-115`). **Documented as
  best-effort, with named gaps** (AC6): the sandbox (env-strip + egress block) is the control;
  known-not-caught examples pinned in the test comments — the `set` builtin's variable dump, a
  redirection-only read (`while read l; do …; done < .env`), and any unlisted binary reading a
  secret file. Same posture as `git_operations`' documented holes. *Rejected:* grading inside
  `ShellExecuteTool` (wrong seam — the executor runs after the gate decided); a real
  shell parser (Amendment 2-D verified there is no bounded verb set — that is why the level is
  per-executor-profile in the first place).

- **D8 — The audit-secrets pin bites at the projection layer, and its red state is proved by
  mutation.** `agent-action:audit-secrets` is an LLM prompt action (RolePhaseMap); what it can
  ever read is what the metadata surfaces return. The pin: (a) reflection over every
  `ISecretQueryService` method's return object graph asserting the **exact** property-name sets
  of the DTOs (so any added member is red, then reviewed) and that no member name matches
  `plaintext|ciphertext|value|material`; (b) a source-shape assertion that
  `SecretQueryService.cs` references neither `ISecretStore`/`ISecretStoreBackend` nor
  `IKekProvider` (the decrypt path). Red state demonstrated by mutation (add a `Plaintext`
  property to `ProjectVersion`'s DTO → red), the same discipline `KnownUngovernedEndpoints`'
  F3 comment records. *Rejected:* grepping the SQL (EF Core composes it; brittle).

- **D9 — "Both profile arms in CI" means parameterized tests, not a second CI leg.** The
  catalog is static per process, so a CI matrix would need two full test runs to flip the
  ambient. Instead: the level pin parameterizes over the internal
  `BuildDescriptors(shellSandboxed:)` seam (`[TestCase(true)] [TestCase(false)]`), and the
  tool-level tests construct `ShellExecuteTool` with in-memory configuration for each profile.
  One `dotnet test` run exercises both arms. *Rejected:* a CI env matrix (cost; and the
  static-catalog ambient cannot be re-initialized in-process anyway).

## Implementation Steps

Lanes: steps 1–3 are self-contained (no catalog, no pins — can land first, in order). Step 4 is
the pin-file lane (serialize with 43-12/31-13). Step 5 is blocked on 43-11. Steps 6 and 8 are
blocked on 43-13. Step 7 serializes with 43-14. Step 9 is independent.

1. **CREATE `src/Tamma.Activities/LlmCall/Tools/ProcessEnvironmentAllowlist.cs`; MODIFY
   `ShellExecuteTool.cs` (after `:94`), `RunTestsTool.cs` (after `:124`)** (AC1, D1) — clear +
   repopulate `psi.EnvironmentVariables` from the base allowlist + `Tools:Shell:EnvAllowlist`.
   Both tools already take `IConfiguration`. *Effort: 0.5 day.*

2. **MODIFY `src/Tamma.Activities/LlmCall/Tools/ShellExecuteTool.cs`; CREATE
   `WorkspaceConfinementScreen.cs` (same dir)** (AC4, D2) — when `Tools:Shell:Sandboxed=true`,
   screen the command before spawn: reject absolute paths outside `_workspaceRoot`, `..`
   traversal that escapes it (resolve path-shaped tokens via `PathValidator.ResolveSafePath`),
   and `cd` to any directory outside the root; the rejection is a `ToolExecutionResult` failure
   with a validation message (the `CommandValidator` blocked-pattern shape at `:72-75`).
   Unsandboxed behaviour byte-identical, pinned. *Effort: 0.5 day.*

3. **CREATE `src/Tamma.Api/Services/Tools/ShellSandboxStartupValidator.cs`; MODIFY
   `src/Tamma.Api/Program.cs`** (AC2, D3) — the fail-loud hosted validator (register beside
   `ActionCatalogStartupValidator`); egress attestation + optional must-fail probe; CWD
   config verification; `TammaError` code `TOOLS.SHELL.SANDBOX_UNVERIFIED`. *Effort: 0.5 day.*

4. **Mint `effect:secret.read`** (AC9 first half, D5) — **MODIFY**
   `src/Tamma.Core/Actions/ExternalEffect.cs` (member + wire, adjacent to `SecretReveal`),
   `src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs` (descriptor, level 90 explicit), and
   the pin files in the same commit: `ActionVocabularyCountTests.cs:53-81` (39→40 + history
   line naming 42-10) and `:132-149` (197→198, rename `TotalCatalogMembers_is_198`, history
   line), `ActionWirePinTests.cs:40-66` (+`"secret.read"` in enum order),
   `ActionGroupMembershipTests.cs:221-227,288` (Secrets 4→5),
   `MediationClientEffectSweepTests.cs:111-215` (+`[SecretRead] = RouteOnly` entry; no client
   method, so `KnownNonEffectClientMethods` stays 19 and the surface pin stays 37).
   **If this lands before 43-11**: also carve `effect:secret.read` out of
   `ActionCatalogDefaultsTests.EveryOtherMember_DefaultsToMin` (`:83-91`) with a comment naming
   this story; if after, 43-11's rewritten table already has a slot — add the row at 90.
   Note: level 90 is valid under today's `[70,100]`, so this step does **not** wait for 43-11.
   *Effort: 0.5 day, mostly pin bookkeeping.*

5. **Profile-dependent shipped level — AFTER 43-11 LANDS** (AC3, D4) — **CREATE**
   `src/Tamma.Core/Actions/ShellExecutionProfile.cs`; **MODIFY**
   `ActionCatalog.Descriptors.cs` (the `tool:shell_execute` and `effect:process.spawn` rows
   read `ShellExecutionProfile.ShippedMinAutonomy`), `ActionCatalog.cs` (internal
   `BuildDescriptors(bool)` seam), `src/Tamma.Api/Program.cs` +
   `src/Tamma.ElsaServer/Program.cs` (call `Initialize` from config at composition, before any
   catalog touch), `src/Tamma.Api/Services/Actions/ActionCatalogStartupValidator.cs`
   (profile↔catalog consistency check); **MODIFY 43-11's level-table pin test** to parameterize
   the two rows on the profile (`[TestCase]` over the build seam, both arms in one run — D9).
   Blocked before 43-11: 40 fails `ActionCatalog.BuildIndex` (`:181-184`) while
   `AutonomyDial.Min = 70`, and moving shell 70→80 pre-43-11 is exactly the shipped-dial
   breakage 43-11's own plan flags. *Effort: 1 day.*

6. **Reveal-route gate — AFTER 43-13 LANDS** (AC5, D6) — **MODIFY**
   `src/Tamma.Api/Program.cs:2602` (add `.Governs(new ActionKey(Effect, "secret.read"))` +
   `.EnforcesGovernance()`),
   `tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs` (delete `:428-429`; `PinnedCount`
   216→215; `PinHistory` → `[237, 216, 215]`; `PinnedInScopeCount` 239 unchanged — the route
   stays in scope, now bound),
   `tests/Tamma.Api.Tests/Actions/GovernedEndpointEnforcementSweepTests.cs:70-93,137` (opt-in
   list 16→17 with the reasoning in the doc-comment, `HaveCount(17)`);
   `ActionEnforcementSitesTests.cs:169,200` move 21→22 in the same commit. *Effort: 0.5 day.*

7. **Seam B shell secret-read grading — SERIALIZE with 43-14's Seam B step, either order**
   (AC6, D7) — **CREATE** `src/Tamma.Api/Services/Actions/ShellSecretReadScreen.cs` (+ options
   record); **MODIFY** `src/Tamma.Api/Services/Agents/CatalogDefaultToolLoopAutonomyGate.cs`
   (`TryResolveKey` gains the shell branch beside the git branch at `:259-262`) and
   `src/Tamma.Api/Extensions/ActionCatalogGovernanceServiceCollectionExtensions.cs:112-115`
   (pass the options). Docs + test comments state best-effort and name the gaps. *Effort:
   0.5 day.*

8. **`secret.reveal` machinery pin — AFTER 43-13 LANDS** (AC8) — **MODIFY** 43-13's machinery
   fixture (its 42-row inventory already contains `effect:secret.reveal`): add the explicit
   assertions that the descriptor is machinery/off-dial, that its audit row emission
   (`SecretRevealService`) and `RevealTokenSweeper` expiry are unchanged (referencing their
   existing fixtures), and that no dial semantics attach. *Effort: 0.25 day.*

9. **audit-secrets metadata-only pin** (AC7, D8) — **CREATE**
   `tests/Tamma.Api.Tests/Secrets/Query/SecretQueryMetadataOnlyTests.cs`. *Effort: 0.25 day.*

10. **`dotnet test` full run** (AC9 second half, D9) — both profile arms exercised by
    parameterization in one run; `dotnet ef migrations has-pending-model-changes` clean (this
    story touches no entity). *Effort: 0.25 day.*

Total ≈ 4.25 days (story estimated 3–4; the overage is the pin-file serialization overhead with
43-12/31-13).

## Test Plan

NUnit + FluentAssertions; `GovernanceHostFixture` for the route sweeps; in-memory
`IConfiguration` for tool construction. **Every test's red state against today's tree is stated;
a pin whose red state is only reachable by mutation says so.**

- **`ProcessEnvironmentAllowlistTests` + `ShellExecuteToolTests` additions** (AC1) —
  `ChildEnvironment_IsExactlyTheAllowlist_BothProfiles`: set canary vars in the test process
  (`GITHUB_TOKEN`, `JWT_SECRET`, `ConnectionStrings__ControlPlane`, a `Tamma:ApiToken`-derived
  name), run `env` through the tool, assert output contains **no** canary and only allowlisted
  names; `[TestCase(sandboxed: true)] [TestCase(false)]`. **Red today**: the child inherits the
  parent env, so the canaries appear in stdout — fails against `ShellExecuteTool.cs:86-94` as
  written. `AdditiveAllowlist_IsHonoured` (configured name passes through). Same pair on
  `RunTestsToolTests`. Red for the same reason.
- **`WorkspaceConfinementTests`** (AC4) — sandboxed: `cat /etc/passwd`, `cat ../../secret`,
  `cd / && ls` each return a validation failure; **red today** (no screen exists — the commands
  execute and succeed). `Unsandboxed_IsByteIdenticalToToday`: the same commands run (pin;
  green today by construction — its job is to go red if someone profile-gates the wrong arm;
  stated as a guard, not evidence).
- **`ShellSandboxStartupValidatorTests`** (AC2) — `SandboxedWithoutAttestation_RefusesStart`
  (calls `StartAsync`, asserts `TammaError` `TOOLS.SHELL.SANDBOX_UNVERIFIED`);
  `ProbeThatConnects_RefusesStart` (loopback listener as ProbeHost — connect succeeds ⇒ egress
  open ⇒ throw); `Unsandboxed_IsANoOp`. **Red today**: the validator type does not exist; the
  first two fail at compile/registration, which is the honest red for a new fail-loud guard.
- **Catalog pins** (AC9/step 4) — before the pin edits land, `ExternalEffect_has_39_members`,
  `TotalCatalogMembers_is_197`, the wire pin, the Secrets-group pin and the mediation-sweep
  totality **all go red on the enum/descriptor commit** — that is the drift machinery working,
  and the pin edits in the same commit are the reviewed resolution (the 43-12 plan's discipline,
  adopted verbatim).
- **`ShellLevel_IsProfileDependent`** (AC3, in 43-11's rewritten level-table fixture) —
  `BuildDescriptors(sandboxed: false)` ⇒ both rows 80; `(true)` ⇒ both rows 40. **Red today**
  twice over: the seam does not exist, and both rows ship `AutonomyDial.Min`.
- **`RevealRouteGovernanceTests`** (AC5) — `LlmCaller_Below90_Is409WithPendingAuthorization`
  (anonymous and engine-token callers; asserts 409, `ACTION.GATE.REQUIRES_HUMAN`, actionable
  `authorizationId`); `HumanCaller_IsUntouched` (authenticated user principal; 200/410 exactly
  as today); `CorrelationGrant_Covers` (43-14 semantics — a granted correlation passes).
  **Red today**: the route carries no binding and no filter — every arm returns the ungated
  response, so the 409 assertions fail.
- **`ShellSecretReadScreenTests` + `ToolLoopAutonomyGateTests` additions** (AC6) — `env`,
  `printenv`, `cat .env` each resolve to `effect:secret.read` and deny at dial 70 (90 > 70);
  a plain `ls` still resolves to `tool:shell_execute`. **Red today**: `TryResolveKey` maps all
  four to `tool:shell_execute`, which is allowed at Min — the deny assertions fail. The fixture
  carries the documented-gap comments (D7) including one **negative** pin:
  `RedirectionOnlyRead_IsNotCaught_KnownGap` (asserts the gap exists so its silent closure — or
  silent widening — is a reviewed event).
- **`SecretQueryMetadataOnlyTests`** (AC7) — exact DTO member-name pins + the no-decrypt-path
  source assertion. **Cannot fail against today's code** (the projections are already
  metadata-only); red state proved by mutation in review (add `Plaintext` to a DTO ⇒ red), per
  D8 — recorded as a guard, not evidence.
- **Machinery pin additions** (AC8, in 43-13's fixture) — `secret.reveal` is machinery,
  level-free, audit row + sweeper untouched. Red-by-mutation (delete the inventory row or give
  it a level ⇒ red). Cannot be written before 43-13's fixture exists.

## Count pins moved (values read from the tree, 2026-08-03)

Assumes 42-10 applies to today's tree; per the 43-12 rule, whichever of 42-10 / 43-12 / 31-13
lands second **rebases** these numbers (+8 keys if 43-12 landed first; +N for 31-13's keys).
Serialize the pin-file commits — they are one shared mutable surface.

| Pin | Site | Before | After |
|---|---|---|---|
| ExternalEffect members | `ActionVocabularyCountTests.cs:53-81` | 39 | **40** (+ history line naming 42-10) |
| Total catalog members | `ActionVocabularyCountTests.cs:132-149` | 197 | **198** (test renamed, history line) |
| Effect wire list | `ActionWirePinTests.cs:40-66` | 39 strings | **40** (`"secret.read"` in enum order) |
| Secrets group members | `ActionGroupMembershipTests.cs:221-227` | 4 | **5** |
| Group count table | `ActionGroupMembershipTests.cs:288` | 4 | **5** |
| Effect→site map | `MediationClientEffectSweepTests.cs:111-215` | 39 entries | **40** (`RouteOnly`, no client method) |
| `KnownNonEffectClientMethods` | `MediationClientEffectSweepTests.cs:800` | 19 | **19 — unchanged** (no client method added) |
| Client-surface pin | `MediationClientEffectSweepTests.cs:664` | 37 | **37 — unchanged** |
| Rows with live sites | `ActionEnforcementSitesTests.cs:169` | 21 | **22** |
| Bound routes | `ActionEnforcementSitesTests.cs:200` | 21 | **22** |
| Ungoverned baseline | `KnownUngovernedEndpoints.cs:221,235` | 216, `[237,216]` | **215, `[237,216,215]`** (entry `:428-429` deleted in the same diff) |
| In-scope surface | `KnownUngovernedEndpoints.cs:250` | 239 | **239 — unchanged** (bound ≠ out of scope) |
| Enforcement opt-in set | `GovernedEndpointEnforcementSweepTests.cs:70-93,137` | 16 | **17** |
| `EveryOtherMember_DefaultsToMin` | `ActionCatalogDefaultsTests.cs:83-91` | all-non-sentinel = Min | pre-43-11: + `secret.read` carve-out; post-43-11: superseded by the rewritten level table |
| Shell/process.spawn shipped level | 43-11's level-table pin (post-rewrite) | 80 static (43-11) | **profile-parameterized 40/80** |

## Risks

- **Env-stripping breaks a toolchain that needed an inherited variable** (`DOTNET_ROOT`,
  `NUGET_PACKAGES`, proxy vars). Mitigation: the additive `Tools:Shell:EnvAllowlist` is a
  deployment knob, the failure mode is a visible tool error naming the missing variable class,
  and the default list includes the POSIX basics. This risk is the price of the P0 fix and is
  accepted.
- **The sandbox attestation can be honestly wrong.** A deployment can declare `firewall` while
  the firewall allows egress; the probe only catches what it can reach. Stated plainly in the
  validator's doc and the story docs: the profile declares the guarantee, the deployment owns
  it, the probe is a tripwire not a proof. The level-40 discount rides on the attestation.
- **CWD confinement is a command-string screen, not a jail.** Interpreters and relative
  symlinks can escape a string screen (heavier isolation is explicitly out of scope). The
  screen's job is the AC4 shapes; the docs say so, mirroring the `git_operations` posture.
- **Grading `env` to `secret.read` denies it at the shipped dial** — a behaviour change in the
  tool loop the day step 7 lands: `env`/`printenv`/secret-path reads 409 at dial 70. Intended
  (that is the ungoverned-`secret.read` closing), and post-D1 the child env is boring, so model
  workflows lose nothing real. Release note required all the same.
- **Anonymous human reveal gets the LLM treatment** (Correction 3 / D6). Recorded; the
  dashboard path (authenticated) is untouched.
- **Static-init ordering on the profile ambient.** If any code touches `ActionCatalog` before
  `Initialize`, the catalog freezes on the default (unsandboxed/80). Mitigated fail-loud by the
  D4 startup consistency check — a wrong ordering is a boot failure, never a silently wrong
  level.
- **Pin-file merge contention with 43-12 and 31-13** is the single biggest schedule risk —
  three stories editing five shared pin files. The serialization rule (43-12's plan) is adopted:
  land pin commits one story at a time; the second and third rebase counts.

## Blocks / Blocked by

- **Blocked by Story 43-11** (hard, step 5 / AC3): `AutonomyDial.Min` 70→1 (a 40 default is a
  boot failure today, `ActionCatalog.cs:181-184`); the rewritten defaults/level-table tests this
  story parameterizes; the shell/process.spawn 80 assignment this story makes profile-dependent.
  Steps 1–4 and 9 do **not** wait.
- **Blocked by Story 43-13** (hard, steps 6 and 8 / AC5 and AC8): `CallerKind` +
  `CallerKindResolver` + the Seam C Human short-circuit; the 42-row machinery fixture AC8
  extends. 43-13's own plan records this dependency in both directions.
- **Story 43-14** (soft, step 7): no code dependency — a `secret.read` ask is coverable by a
  correlation-standing grant by inheritance — but its Seam B step edits the same region as the
  grading; the two lanes are serialized (either order), per 43-14's plan.
- **Story 43-12** (coordination only): no key overlap; same five pin files — serialize pin
  commits, second-lander rebases counts. Also note 43-12 splits merge/deploy keys; none of them
  touch this story's rows.
- **Story 31-13** (coordination only): same pin-file contention; its git route/`GitEndpoints`
  edits do not intersect this story's files.
- **Stories 43-15, 43-16, 39-25, 40-8**: disjoint. 43-15 will render the profile-dependent
  level and the machinery flag it gets from 43-13 — copy coordination only. 40-8's
  `EngineEndpoints.cs` work does not touch any file named here.
- **Story 29-1**: landed (`ISecretStore` et al. under `src/Tamma.Api/Services/Secrets/`) — the
  dependency in the story text is satisfied; nothing to wait for.

## Blocked / contradictions

Nothing unpassable was found. Three items short of clean, none silent:

1. **AC3's sandboxed arm is unimplementable on today's tree** (40 < `AutonomyDial.Min`, boot
   guard at `ActionCatalog.cs:181-184`). Not a contradiction in the story — the story's own
   Dependencies name 43-11 — but the plan states it as a hard gate on step 5, not a soft
   ordering preference.
2. **AC5's "human caller is untouched" holds only for identifiable humans.** The reveal route
   is deliberately anonymous, and 43-13 grades anonymous as LLM (fail-closed). An
   unauthenticated human exchange (pasted curl) will 409 below dial 90. The authenticated
   dashboard path is untouched. Recorded in D6/Corrections and pinned in the AC5 tests rather
   than resolved away — resolving it by trusting anonymity would un-gate the exact bypass AC5
   exists to close.
3. **The story says "18-pattern denylist"; the tree has 16** (`CommandValidator.cs:16-59`).
   Cosmetic; corrected here.

## Definition of Done

| AC | Step(s) | Verified by |
|---|---|---|
| 1 — child env is the allowlist, always | 1 | `ChildEnvironment_IsExactlyTheAllowlist_BothProfiles` (+ RunTests arm) |
| 2 — profile declared, verified, fail-loud | 3 | `ShellSandboxStartupValidatorTests` (three tests) |
| 3 — shipped level 40/80 by profile | 5 | `ShellLevel_IsProfileDependent` (both arms, one run) |
| 4 — CWD confinement sandboxed; unsandboxed unchanged | 2 | `WorkspaceConfinementTests` (both arms) |
| 5 — `secret.read` at 90, reveal route gated for LLM, human untouched | 4, 6 | `RevealRouteGovernanceTests` (three tests) |
| 6 — shell grading fires, best-effort stated, gap named | 7 | `ShellSecretReadScreenTests` incl. `RedirectionOnlyRead_IsNotCaught_KnownGap` |
| 7 — audit-secrets metadata-only | 9 | `SecretQueryMetadataOnlyTests` |
| 8 — `secret.reveal` off the dial, plumbing intact | 8 | machinery-fixture additions |
| 9 — count pin +1 with history line; both arms green | 4, 10 | the pin table above; one `dotnet test` run |

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-03 | 1.0.0   | Initial plan. All story citations re-verified against the tree (one correction: 16 denylist patterns, not 18). Nine design decisions incl. the catalog-build profile ambient (D4), fail-closed anonymous reveal (D6), and resolution-time shell grading (D7). Pin table read from the tree; serialization rule with 43-12/31-13 adopted. Hard gates: 43-11 for AC3, 43-13 for AC5/AC8. | Claude |
