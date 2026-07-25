# Finding: Prompts are deliberately provider-invariant

**Date**: 2026-07-25
**Author**: Claude
**Type**: 📐 Architecture Decision Record (informal) / 💡 Lesson Learned
**Category**: Architecture

## 📋 Summary

Question raised: *"How do we handle provider-specific prompting — if a provider
needs simpler prompts, or the reverse? Is that even needed?"*

**Answer: no, and the current design is already correct.** Prompt resolution is
keyed `(principal, scope, role, action)` with **no provider dimension**, and it
should stay that way. Provider differences belong at the transport seam, not in
the prompt. The abandoned per-provider hierarchy that still existed as dead code
(`ResolveLlmPromptActivity`) has been deleted so it stops reading as a supported
design.

## 🔍 Context

Two things looked like a provider dimension existed:

1. `ResolveLlmPromptActivity` shipped a 6-level config hierarchy whose top two
   levels were `LlmPrompts:{provider}:{role}` and `LlmPrompts:{provider}:default`.
2. `HttpProviderClient` branches on the provider key.

(1) was **dead** — zero call sites, superseded by `ResolvePromptFromRegistryActivity`
when prompts moved to the Prompt Store. It has been removed (this change), along
with the `ResolvedPrompt` class and `PromptResolutionLevel` enum in
`LlmCallModels.cs`, plus its UIHint test case. No `LlmPrompts:*` key was ever set
in any `appsettings*.json`, so nothing observable changes.

(2) is transport, not prompting — see below.

### Related Components
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` (live resolver)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` (`BuildRetryLoop`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RepairRingOptions.cs`

## 💡 The Findings

### 1. The provider chain is a retry fallback — that *forces* provider-invariant prompts

`LlmCallWorkflow.BuildRetryLoop` runs `ForEach<provider>`: the **same** call is
retried against the next provider in the chain when one fails. If the prompt were
provider-keyed, the prompt would silently change mid-retry — while the parsed
output contract still has to hold identically, because the caller has already
bound to one contract.

So provider-invariance is not a simplification we chose for tidiness. It is a
**structural requirement of the fallback design**. Adding a provider axis to the
prompt store would put the retry loop and the contract layer in direct conflict.

### 2. Contract pinning makes the axis expensive

One `(role, action)` cell = one contract, CI-enforced by `ContractBindingTests`.
The taxonomy is 8 roles × 10 actions = 80 cells. A provider axis over 8+ supported
providers turns that into 640+ cells, each needing its own pinned contract and its
own drift test. The maintenance cost is superlinear and the benefit is speculative.

### 3. "A weaker model needs a simpler prompt" is the wrong fix

This is the instinct that leads to forking. Current guidance on context
engineering points the other way: over-constraining a capable model degrades it,
and the fix for bad output is a **better-designed interface** — clearer tool
contracts, progressive disclosure, fewer worked examples — not a per-model prompt
variant. (Claude Code removed >80% of its system prompt with no eval regression.)

A prompt tuned down for the weakest provider in the chain also drags down every
stronger provider that shares the cell.

### 4. The two seams that legitimately need provider awareness already exist

| Difference | Where it belongs | Status |
|---|---|---|
| Wire/auth shape, system-role slot, tool-schema encoding, thinking blocks | `HttpProviderClient` (already branches on provider key) | ✅ exists |
| Malformed / off-contract output from a weaker model | the deterministic repair ring (`RepairRingOptions`) | ✅ exists |

Note `RepairRingOptions`' own comment: widening it requires **observed
real-provider failure-rate evidence** recorded in `.dev/findings/`. The codebase
already refuses provider-specific tuning without data. That bar should apply to
any future proposal for a provider dimension too.

### 5. If a provider ever genuinely cannot share a cell

The escape hatch is **capability, not prompt text**: the per-cell knobs
(`enableTools`, `maxTokens`) already exist in the prompt front matter, and a
provider that cannot satisfy a cell's contract should be **excluded from that
cell's provider chain** — a routing decision — rather than given a private copy
of the prompt.

## ✅ Action Items
- [x] Delete `ResolveLlmPromptActivity` + `ResolvedPrompt` + `PromptResolutionLevel`
      + the UIHint test case.
- [x] Correct `wiki/Architecture.md` §7.3, which still listed the deleted activity
      as a live stage of the LLM call pipeline.
- [x] Mark `.dev/findings/resolve-llm-prompt-missing-store-query.md` resolved-obsolete.
- [ ] The cross-provider improvement that *is* warranted — trimming over-constraint
      out of the 80 cells — is filed as **Story 39-22** (prompt-quality pass).

## 🔗 Related
- `docs/stories/epic-39/story-39-22-prompt-quality-pass.md`
- `.dev/findings/resolve-llm-prompt-missing-store-query.md`
- `CLAUDE.md` § Prompt Store Architecture (the `(principal, scope, role, action)` key)

## 📊 Impact Assessment
**Severity**: 🟢 Low — the deletion is behavior-neutral (dead code, no config keys
set). The value is in removing a misleading design signal and recording *why* the
axis is absent, so it is not "fixed" later by someone reading the gap as an omission.

---

**Status**: ✅ Resolved (one follow-up: Story 39-22)
**Last Updated**: 2026-07-25
