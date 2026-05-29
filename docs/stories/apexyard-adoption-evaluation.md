# ApexYard Pattern Adoption Evaluation

**Status**: Draft proposal — pending user decision
**Author**: Claude (research)
**Date**: 2026-05-29
**Type**: Evaluation / proposal (NOT an epic — placement deliberate)
**Related**: `.dev/decisions/`, `.github/agents/tamma-reviewer.md`, `apps/tamma-elsa/src/Tamma.Data/Migrations/`

## 1. Why consider ApexYard patterns

[ApexYard](https://github.com/me2resh/apexyard) is an opinionated, MIT-licensed SDLC governance framework (≈259 stars, very active) that has crystallized several patterns Tamma is currently approximating with custom conventions and ad-hoc commit-message discipline. Tamma already has strong primitives in place — `.dev/decisions/` ADRs, a `tamma-reviewer` subagent, structured CI (`.github/workflows/ci.yml`), DCB event sourcing for runtime decisions — but several touchpoints (AI-decision capture, migration discipline, mechanical merge gating) are still informal. This document evaluates eight ApexYard patterns against Tamma's current state and proposes a small, focused adoption set; it does not propose adopting ApexYard as a framework or forking its repo, which would clash with Tamma's monorepo structure.

## 2. Pattern-by-pattern evaluation

| # | Pattern | Already in Tamma? | Value | Effort | Recommendation |
|---|---|---|---|---|---|
| 1 | **AgDR (Agent Decision Record)** | Partial — `.dev/decisions/` exists with a generic decision template (`.dev/templates/decision-template.md`); no dedicated section for AI-specific reasoning / alternatives / agent prompts. ADR-004, story-28-1-design-calls.md etc. capture decisions but in human-author voice. | **High** — Tamma is uniquely AI-driven (provider selection, prompt resolution, convention override, agent role/action choices). Capturing AI reasoning explicitly closes the loop for self-maintenance and audit. | **S** (one new template + thin index) | **Adapt** — add an AgDR template alongside the existing decision template; do not replace ADRs. |
| 2 | **Migration AgDR** | **No** — 17 EF migrations under `apps/tamma-elsa/src/Tamma.Data/Migrations/` exist; story 28-1 and the audit doc cover the high-level intent, but per-migration rollback / data-volume / cross-service-consumer notes do not exist as a discipline. Most migrations have a stub `Down()` method but no narrative. | **High** — Epic 28 (db-per-tenant) ships RLS + multi-context schemas; an undocumented rollback risk is a real production hazard. | **S–M** (template + first-class enforcement decision) | **Adopt** — mandatory for any future migration touching CP / tenant / Elsa contexts. |
| 3 | **Two-marker merge gate (Rex + human)** | **No mechanical gate** — `tamma-reviewer.md` is a subagent (advisory only); `.github/CODEOWNERS` requires owner approval for workflows/docker; no PR-level "AI-reviewed + human-approved" marker pair enforced via hook or required check. | **Medium** — Tamma is solo-maintained today; the value of a *two-marker* gate is lower than in a team setting. But the *AI-reviewed marker as a required check* is high-value once GitHub App / SaaS mode goes live. | **M** (status-check workflow + marker file convention) | **Adapt** — adopt a single "tamma-reviewer ran and passed" required check now; defer the second human-approval marker (it would duplicate GitHub's existing required-reviewers feature in single-maintainer mode). |
| 4 | **Migration ticket hook (`require-migration-ticket.sh`)** | **No** — nothing blocks edits under `Migrations/**`. `.github/workflows/forbidden-symbols.yml` shows the pattern (CI grep guard) is already established in Tamma. | **High** — pairs directly with #2; without an enforcement hook, the Migration AgDR template is just a suggestion. | **M** (CI job + path-rule list + bypass-label) | **Adopt** — implement as a CI job (path-filtered) rather than a local shell hook so it works for both `git push` and PRs uniformly. |
| 5 | **Portfolio aggregation (`/inbox`, `/status`, `/tasks`)** | **No** — Tamma is a single monorepo; no cross-repo aggregation needed. Future: Epic 28 may produce per-tenant Elsa repos but those are *generated*, not human-authored. | **Low** — solves a problem Tamma does not have today. | **L** (would require building inbox / config infra) | **Skip** — revisit only if Tamma ever splits into multiple human-authored repos. |
| 6 | **SHA-bound approval markers + stale-marker warning** | **No** — GitHub's "dismiss stale reviews when new commits are pushed" branch-protection rule already covers the equivalent for human approvals. The AI-reviewer marker (see #3) would need its own staleness handling. | **Medium** — only valuable if #3 is adopted; without it, GitHub's built-in handles the case. | **S** (small extension to the #3 marker job) | **Adapt** — fold into #3's implementation (re-run AI review check on push); no separate adoption needed. |
| 7 | **Path-mirroring customization overlay (`custom-templates/`, `custom-skills/`, `custom-handbooks/`)** | **N/A** — Tamma's customization story is *runtime, database-driven* (prompt_overrides, convention overrides via `IPromptStore` + tenant resolution); the path-mirroring overlay solves customization at the *framework / source-tree* level. Different design axis entirely. | **Low** (for Tamma) — would conflict with Tamma's per-tenant DB-stored override model documented in CLAUDE.md "Prompt Store Architecture". | **N/A** | **Skip** — fundamentally incompatible with Tamma's tenant-isolated DB-overrides design. |
| 8 | **Local agent routing pipeline + SessionStart reachability check / INACTIVE warning** | **Partial** — local LLM provider plumbing exists (`packages/providers/src/` references ollama/openrouter); no startup health-probe + INACTIVE warning surfaced through the CLI / API as a first-class banner. | **Medium** — quality-of-life for the self-hosted (`tamma start` / `tamma server`) mode; less critical for SaaS (`tamma api`) which uses managed providers. | **S** (one-shot probe + cached state + CLI/API surface) | **Adopt** — small, isolated, high user-visibility for self-hosted operators. |

### Pattern-fit quick map

- **Patterns that fit Tamma's architecture cleanly**: #1 AgDR, #2 Migration AgDR, #4 Migration ticket hook, #8 Local provider reachability.
- **Patterns that fit *if* adapted for a solo-maintainer-then-team trajectory**: #3 Two-marker gate (downscoped to single AI-reviewer check), #6 stale-marker handling (folded into #3).
- **Patterns that do not fit**: #5 Portfolio aggregation (wrong shape — single repo), #7 Path-mirroring overlay (collides with Tamma's DB-driven override model).

## 3. Recommended adoption set

Four patterns recommended for adoption, in priority order.

### 3.1. Adopt — Migration AgDR + ticket hook (patterns #2 + #4 together)

**Why first**: Epic 28's database-per-tenant work is the riskiest active surface in the codebase. 17 EF migrations have shipped without a uniform rollback / consumer-impact narrative; the next migration could break tenant provisioning silently. Patterns #2 and #4 only deliver value as a pair (template without enforcement = decoration; enforcement without template = noise), so they ship together.

**Concrete adoption task**:
1. Create `.dev/templates/migration-agdr-template.md` based on `.dev/templates/decision-template.md` with mandatory sections: *Migration name*, *DbContext touched*, *Down-method status*, *Rollback plan* (SQL or migration name to revert to), *Estimated downtime / lock duration*, *Cross-context consumers* (RLS policies, EF queries, Elsa workflow definitions), *Data volume at apply time*, *Testing plan* (testcontainers + reset-all script), *Observability* (what to grep in logs to confirm success), *Agent reasoning* (if AI proposed the schema change).
2. Create `.github/workflows/require-migration-agdr.yml` mirroring the structure of `forbidden-symbols.yml`. CI job runs on PRs that touch `apps/tamma-elsa/src/Tamma.Data/Migrations/**` or `*Migration*.cs` and fails unless either (a) a file matching `.dev/decisions/migration-*.md` is in the same PR, or (b) the PR carries a `skip-migration-agdr` label whose addition is restricted to CODEOWNERS via a separate workflow.
3. Backfill is **not required** for the 17 existing migrations — guard the rule with a "from this date forward" file allowlist or by relying on the path filter only firing on new files.

**File paths created or modified**:
- `.dev/templates/migration-agdr-template.md` (new)
- `.github/workflows/require-migration-agdr.yml` (new)
- `.dev/decisions/README.md` (update — document the migration-agdr naming convention)
- `CLAUDE.md` (update — add a short "Migration AgDR required for new schema work" note in the Epic 28 section)

**Acceptance criteria**:
- Template covers all eight mandatory sections; a worked example is committed alongside (e.g. a retroactive migration-agdr for the most recent migration as the reference).
- CI job fails a PR that adds a `*Migration*.cs` file without a matching `.dev/decisions/migration-*.md` file (verified via a deliberate test PR).
- CI job passes when both files are present.
- Label-based bypass is restricted to CODEOWNERS and emits an audit event in the PR comments.
- Documentation in `.dev/README.md` explains the discipline.

### 3.2. Adopt — AgDR template (pattern #1)

**Why second**: Tamma already has ADRs but they are written in a human-author voice that hides the AI's reasoning. As Tamma moves toward self-maintenance (Epic-2-built-Epic-3 milestone), the AI's own decision trail becomes part of the audit surface. AgDRs are *additive* — they sit next to ADRs, not replacing them.

**Concrete adoption task**: Create `.dev/templates/agdr-template.md` that distinguishes from the existing decision template by mandating: *Prompt or trigger that initiated the decision*, *Agent role and provider*, *Alternatives the agent considered* (verbatim, not summarized), *Confidence signal* (if available from provider), *Tool calls made during deliberation*, *Human override / acceptance status*, *Linked DCB event IDs* (so the doc cross-references the event store). Filename convention `.dev/decisions/agdr-YYYYMMDD-<slug>.md`. Update `.dev/README.md` to document when to use AgDR vs ADR (rule of thumb: if a human alone could have made the call, it is an ADR; if the AI's reasoning is load-bearing, it is an AgDR).

**File paths created or modified**:
- `.dev/templates/agdr-template.md` (new)
- `.dev/README.md` (update — add AgDR section)
- `.github/agents/tamma-reviewer.md` (update — review checklist gains "if decision was AI-led, was an AgDR captured?" item)

**Acceptance criteria**:
- Template exists and is referenced from `.dev/README.md`.
- At least one worked AgDR is committed (recommend: retroactively for the Epic 28 KEK backend decision already captured in memory at `project_epic28_kek_decision.md`).
- `tamma-reviewer` checklist updated.
- No CI enforcement at this stage — discipline only, since the boundary between ADR and AgDR is fuzzy enough that mechanical enforcement would generate noise.

### 3.3. Adopt — Local provider reachability check + INACTIVE warning (pattern #8)

**Why third**: Small, isolated, high user-visibility for self-hosted operators. Tamma already differentiates `tamma start` (self-hosted engine) from `tamma api` (SaaS), and self-hosted users are the most likely to point Tamma at a local Ollama or LiteLLM endpoint that may or may not be running.

**Concrete adoption task**: At orchestrator/engine startup, when a local-LLM provider is configured (Ollama, LiteLLM, OpenCode), probe its `/api/tags` (Ollama) / `/models` (LiteLLM) endpoint with a short timeout. Cache the result for the process lifetime. Surface the result via: (a) a CLI banner on `tamma start`, (b) a `GET /api/v1/health/providers` endpoint that returns `{provider, status: 'active'|'inactive'|'unknown', lastChecked}`, (c) a structured log line. Mark the provider INACTIVE in `ProviderRegistry` so the chain skips it without retry storms.

**File paths created or modified**:
- `packages/providers/src/local-reachability.ts` (new — shared probe utility)
- `packages/providers/src/provider-chain.ts` (update — consult cached reachability before routing)
- `packages/api/src/routes/health.ts` (update — expose provider health)
- `packages/cli/src/commands/start.ts` (update — render banner)
- `packages/providers/src/local-reachability.test.ts` (new)

**Acceptance criteria**:
- Startup probe completes within 2s (configurable) and does not block engine boot on failure.
- INACTIVE providers are skipped in the routing chain; chain falls through to next provider without per-request retry.
- CLI shows a yellow `INACTIVE: ollama (localhost:11434 not reachable)` banner when applicable.
- `GET /api/v1/health/providers` returns the cached status; manual re-probe possible via `POST /api/v1/health/providers/:name/probe`.
- Unit test covers reachable + unreachable + timeout cases.

### 3.4. Adopt — AI-reviewer required check (downscoped #3, with #6 folded in)

**Why fourth**: Tamma already has `tamma-reviewer` as a subagent; lifting it from advisory to required-check status is a high-leverage governance move *once SaaS mode goes live*. In current solo-maintainer mode the value is lower, so this is the lowest-priority of the four — adopt only if SaaS launch is imminent.

**Concrete adoption task**: Create `.github/workflows/tamma-reviewer-check.yml` that runs `tamma-reviewer` against the PR diff and posts a structured marker (e.g. PR comment with `tamma-reviewer/sha=<commit>/status=pass|fail`) plus a status check. Add the status check to required-checks for `main`. Re-run automatically on new commits (this folds in pattern #6: SHA-bound markers become stale automatically because each commit triggers a fresh check; no separate staleness logic needed). Do NOT add a second "human approval marker" — GitHub's required-reviewers branch protection already covers that path.

**File paths created or modified**:
- `.github/workflows/tamma-reviewer-check.yml` (new)
- `.github/agents/tamma-reviewer.md` (update — clarify it is now invoked from CI, output format must be machine-parseable)
- `docs/architecture/review-api.md` or new `docs/architecture/pr-review-gate.md` (new — document the gate)

**Acceptance criteria**:
- CI job invokes `tamma-reviewer` against the PR diff and emits a structured pass/fail status check.
- Required-check rule added on `main` (manual repo-settings change, documented in the story).
- Status re-runs on push (no stale-approval risk).
- Fail mode is non-destructive: PR cannot merge but explanation is in the PR comment.
- Bypass label restricted to CODEOWNERS for emergency merges.

## 4. Open questions for user decision

1. **Migration AgDR enforcement strength**: hard block (CI failure on missing AgDR) or soft warning (PR comment only)? Recommendation: **hard block** with CODEOWNER-only `skip-migration-agdr` label. Confirm.
2. **Backfill scope for existing 17 migrations**: leave as-is (recommended) or retroactively author Migration AgDRs for the Epic 28 set?
3. **AI-reviewer required check timing**: adopt now (low value in solo mode but builds the muscle) or defer until SaaS launch?
4. **AgDR vs ADR distinction**: are you comfortable with the "if AI reasoning is load-bearing → AgDR" heuristic, or would you prefer a single decision template with an optional "Agent reasoning" section?
5. **Local provider INACTIVE surface**: CLI banner + API endpoint enough, or also a dashboard widget in `@tamma/dashboard`?

## 5. Out of scope (explicitly NOT recommended)

- **Forking ApexYard as the operations repo**: ApexYard's distribution model assumes the framework *is* the project's ops layer. Tamma is a monorepo product with its own deployment pipeline; forking ApexYard would create a parallel governance tree that would drift immediately. We adopt patterns, not the framework.
- **Portfolio aggregation skills (`/inbox`, `/status`, `/tasks`)**: Tamma is a single repo. Re-evaluate only if Epic 28's per-tenant Elsa instances ever become *human-authored* rather than generated.
- **Path-mirroring `custom-*/` overlay**: directly conflicts with Tamma's DB-stored prompt/convention override model. Tamma's tenant-isolated DB design is *better* for SaaS, not worse — adopting the path-mirror overlay would split the override surface and break tenant isolation.
- **Two-marker merge gate in its full form (Rex + separate human marker)**: in solo-maintainer mode this duplicates GitHub's required-reviewers feature; we downscope to a single AI-reviewer status check (3.4).
- **Replacing existing ADRs with AgDRs**: AgDRs are additive. The existing `.dev/decisions/` ADRs stay as-is.

## 6. Comparison — Tamma's existing conventions vs ApexYard's stack

Tamma and ApexYard solve adjacent but distinct problems. ApexYard governs the *development workflow* (PR review, decision documentation, migration discipline) at the source-tree level using markdown templates, shell hooks, and CI gates. Tamma governs the *runtime workflow* (AI-driven development orchestration) using DCB event sourcing, tenant-aware databases, and a typed provider abstraction layer. The two stacks overlap most at the decision-capture surface: ApexYard's AgDR captures *what the agent decided and why* in markdown, while Tamma's DCB event store captures *what the agent did* in structured events (`CODE.GENERATED.SUCCESS`, `ISSUE.ASSIGNED.SUCCESS` etc.). The patterns that fit Tamma best are the ones that fill gaps the DCB stream cannot cover by design — design-time decisions, pre-merge governance, schema migrations — because the event store is for runtime audit, not design-time reasoning. The patterns that do not fit (#5, #7) are the ones that assume a source-tree-level customization or aggregation model that Tamma has consciously moved to the database layer for tenant isolation reasons. Adoption recommendations in this doc track that boundary deliberately.
