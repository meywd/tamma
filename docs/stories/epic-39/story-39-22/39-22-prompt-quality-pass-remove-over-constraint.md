# Story 39-22: Prompt Quality Pass — Remove Over-Constraint, Measure Don't Guess

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

Specifically required for this story:

- `.dev/findings/no-provider-dimension-in-prompts.md` — **why this story exists**, and
  why it is deliberately a *cross-provider* pass rather than per-provider tuning.

## User Story

As a **prompt author** (and the operator paying for every repair turn),
I want the prose body of each `Prompts/{role}/{action}.md` cell trimmed of over-constraint — rules that substitute for the model's judgement, worked examples that a contract already specifies, and context front-loaded "just in case" — with each change **measured against the repair-ring event stream** rather than accepted on taste,
So that prompts get shorter and cheaper without anyone having to guess whether they also got worse.

## Priority

P2 — Quality/cost work, not a blocker for any other story. Sequence it **after 39-16**
(see Dependencies): 39-16 makes the output-contract block generated content, which
removes the single largest source of hand-written prompt bulk *and* draws a hard line
around the region this story must not touch.

## Architectural Context (READ FIRST)

### The surface

- `apps/tamma-elsa/src/Tamma.Api/Prompts/{role}/{action}.md` — 8 roles × 10 actions =
  **80 cells**, plus `Prompts/{role}/_system.md` role-identity preambles. Front matter
  carries `variables` / `enableTools` / `maxTokens` / `version`.
- `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs` — loads them at startup,
  fail-loud (a taxonomy cell with no file, or a file outside the taxonomy, refuses to
  start). `SystemPrompts` is the facade.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` —
  the live resolver: renders the `(role, action)` cell via
  `POST /api/prompts/{role}/{action}/render`.

### There is no provider dimension, and this story does not add one

Prompt resolution is keyed `(principal, scope, role, action)`. That is correct and
load-bearing: `LlmCallWorkflow.BuildRetryLoop` retries the **same** call across the
provider chain (`ForEach<provider>`), so a provider-keyed prompt would swap the prompt
mid-retry while the output contract still has to hold. Provider differences belong in
`HttpProviderClient` (transport) and the repair ring (off-contract output), both of
which already exist. Full rationale in
`.dev/findings/no-provider-dimension-in-prompts.md`.

**Consequence for this story:** every edit must be an improvement for the *whole*
chain. Tuning a cell down to the weakest provider drags down every stronger provider
sharing it — that is the failure mode to avoid, and AC5 exists to catch it.

### The measurement substrate already exists

Story 39-9 AC6/AC7 shipped `RepairRingEventTypes` — `LLM.VALIDATION.FAILED`,
`LLM.REPAIR.SUCCEEDED`, `LLM.REPAIR.EXHAUSTED` — all tagged
`{ issueId, documentType, role, action, repairTurn, correlationId, tenantId }`
specifically so that **per-`(role, action) × documentType` validation-failure,
first-repair-success and exhaustion rates are computable from the events alone**.

That is the eval signal. This story does not need a new harness; it needs to *use* the
one 39-9 built. A prompt-quality pass without this would be unfalsifiable — which is
the reason to do it now rather than earlier.

### What "over-constraint" means concretely

The pattern to remove, in priority order:

1. **Rules that pre-empt judgement** — "if X then do Y, unless Z, in which case…"
   decision trees the model can derive from the goal + the contract.
2. **Worked examples that restate the contract** — once 39-16 generates the
   output-contract block from `RenderContract`, an example showing the same shape is
   duplicated spec, and it *narrows* the model toward the example's specifics.
3. **Front-loaded context** — background dumped in "just in case" rather than reachable
   on demand. Where a tool or a variable can supply it at the point of need, prefer that.
4. **Defensive boilerplate** — "do not hallucinate", "be accurate", "think step by
   step" and similar, which cost tokens on every call and buy nothing measurable.

The precedent for the size of the available win: Claude Code removed >80% of its system
prompt with no eval regression. Do **not** treat 80% as a target — the target is set by
AC5, per cell.

## Acceptance Criteria

1. **Baseline captured before any edit.** A committed baseline report
   (`docs/stories/epic-39/story-39-22/baseline.md`) records, per cell: current prose
   body token count, and — from the `RepairRingEventTypes` event stream over a stated
   window — validation-failure rate, first-repair-success rate and exhaustion rate. Cells
   with too little traffic to have a rate are listed explicitly as
   **unmeasured**, not silently averaged in.

2. **The prose body is the only region edited.** Front matter (`variables`,
   `enableTools`, `maxTokens`, `version`) is unchanged except `version`, which is bumped
   on every edited cell. The **output-contract block is not touched** — after 39-16 it is
   generated from `RenderContract`, and a hand-edit there is a drift bug, not a quality
   improvement. A test asserts the generated block is byte-identical before and after.

3. **Every removal is justified by class, in the diff.** Each edited cell's PR
   description (or a per-cell line in a summary table) names which of the four
   over-constraint classes was removed. "Shortened" is not a justification.

4. **Contract and taxonomy gates stay green, untouched.** `ContractBindingTests`,
   `PromptFileLoader`'s fail-loud taxonomy check, and the front-matter drift tests pass
   without modification. If a gate has to be *changed* to accommodate an edit, the edit
   is out of scope for this story.

5. **No cell regresses on the repair-ring signal.** For every cell that had a measurable
   baseline (AC1), the post-change validation-failure rate and exhaustion rate are **not
   worse** than baseline over a comparable window, measured across at least **two
   different providers** from the configured chain (the cross-provider requirement — a
   cell that improves on one provider and degrades on another has not improved). A cell
   that regresses is reverted, and the revert is recorded with the observed numbers in
   `.dev/findings/`.

6. **Unmeasured cells are edited conservatively or not at all.** A cell with no baseline
   signal (AC1) may only have class-4 removals (defensive boilerplate) applied. Classes
   1–3 on an unmeasured cell require either generating traffic to measure it first, or
   deferring that cell — recorded either way. **No cell is edited on taste alone.**

7. **Token delta reported, not targeted.** The final report states total and per-cell
   prose-body token reduction. There is **no minimum reduction acceptance threshold** —
   a cell that is already right stays as it is, and AC5 is the gate that matters.

8. **The authoring rule is written down.** `docs/stories/epic-39/prompt-authoring-guide.md`
   (or a section appended to the 39-16 contract-generation doc) states the four
   over-constraint classes, the "one contract, no provider dimension" rule with its
   `BuildRetryLoop` rationale, and the measure-before-you-trim requirement — so new cells
   are authored thin rather than trimmed later.

## Dependencies

- **39-16 (Prompt Contracts Generated From Document Types)** — sequence this story
  *after* it. 39-16 turns the output-contract block into generated content, which both
  removes the largest block of hand-written bulk and makes AC2's "don't touch the
  contract block" enforceable by construction rather than by discipline. Running 39-22
  first is possible but wastes effort on text 39-16 will regenerate.
- **39-9 (Deterministic Repair Ring)** — ✅ landed. Supplies the entire measurement
  substrate (AC1/AC5). Without its per-cell event tags this story has no gate.
- **39-12 … 39-15 (producer migrations)** — ✅ landed. Every document-producing workflow
  now rides the typed lifecycle, so the repair-ring events cover the producing cells
  uniformly. Before these, the signal would have been partial.

## Out of Scope

- **Any provider dimension in prompt resolution** — see
  `.dev/findings/no-provider-dimension-in-prompts.md`. If a provider genuinely cannot
  satisfy a cell's contract, the fix is to exclude it from that cell's provider chain
  (a routing decision), never to give it a private prompt.
- **Front-matter / capability changes** (`enableTools`, `maxTokens`) — those are agent
  configuration, not prompt quality.
- **`_system.md` role-identity preambles** — same four classes apply in principle, but
  they are shared across all 10 actions of a role, so a regression there is 10× as
  broad. Handle as a follow-up with its own baseline, not inside this pass.
- **Tool descriptions** — same design pressure ("simple descriptions beat elaborate
  ones"), different surface. Belongs with Epic 42's tool-descriptor work.

## Est. Effort

4–6 days — dominated by AC1 baselining and AC5 verification windows, not by the editing.
