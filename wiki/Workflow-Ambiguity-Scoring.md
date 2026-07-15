---
title: "Workflow: Ambiguity Scoring"
---

**Definition ID:** `ambiguity-scoring`
**Class:** `AmbiguityScoringWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AmbiguityScoringWorkflow.cs`

## Purpose

The Ambiguity Scoring workflow (Story 3.6) quantifies how ambiguous/underspecified a requirement is: a 0..1 score plus a typed, itemised breakdown with specific recommendations, produced via the MEDIATED `llm-call` path (role=`product_owner`, action=`score-ambiguity`; the engine holds no LLM credential). It then applies a caller-supplied threshold policy to DECIDE whether clarification should be triggered before implementation proceeds, and emits every transition as an `AMBIGUITY.*` DCB event.

Scoring is AUTONOMOUS — there is no human gate/bookmark. The workflow itself does not dispatch clarification: it exposes a `decision` output (`clarify` when score >= threshold, else `proceed`) that a parent flow uses to route into the sibling [Clarifying Questions](/workflows/clarifying-questions) workflow. It reuses the [Research](/workflows/research) / [Assessment](/workflows/assessment) skeleton (llm-call → parse → fail-closed gate + error terminal).

## Flow Diagram

```
+------------------+
| Read Inputs      |
| (requirement,    |
|  context,        |
|  threshold)      |
+--------+---------+
         |
         v
+------------------+
| Emit AMBIGUITY.  |
| STARTED          |
+--------+---------+
         |
         v
+------------------+
| Score Ambiguity  |
| (llm-call:       |
|  product_owner/  |
|  score-ambiguity)|
+--------+---------+
         |
         v
+------------------+
| Parse Ambiguity  |
| (fail-closed)    |
+--------+---------+
         |
         v
+------------------+
| Ambiguity LLM OK?|
+--+------------+--+
  YES            NO
   |              |
   v              v
+----------+ +------------------+
| Emit     | | Emit AMBIGUITY.  |
| AMBIGUITY| | FAILED (LOUD)    |
| .SCORED  | +--------+---------+
+----+-----+          |
     |                v
     v         +------------------+
+----------+   | Ambiguity Error  |
| Compute  |   | (Finish)         |
| Decision |   +------------------+
| (score >=|
| threshold|
|  ?)      |
+----+-----+
     |
     v
+------------------+
| Should Clarify?  |
+--+------------+--+
  YES            NO
   |              |
   v              v
+-----------+ +-----------+
| Emit      | | Emit      |
| CLARIFI-  | | BELOW_    |
| CATION_   | | THRESHOLD |
| TRIGGERED | |           |
+-----+-----+ +-----+-----+
      |             |
      +------+------+
             |
             v
      +------------------+
      | Set Output       |
      | Result           |
      +--------+---------+
               |
               v
      +------------------+
      | Expose Output    |
      | (score, decision,|
      |  assessment)     |
      +------------------+
```

## Threshold Policy

`AmbiguityThresholds` is a pure, unit-testable policy:

- **Default** clarify threshold: `0.5` (used when the caller supplies none).
- A positive caller threshold is clamped to `[0, 1]`; a value <= 0 is treated as "unset" and falls back to the default (a threshold of exactly 0 would make every requirement — including a perfectly clear one — trigger clarification).
- Decision: `score >= threshold` → `clarify`; otherwise `proceed`.

## Sub-Workflows Dispatched

| Workflow | Wait? | Purpose |
|----------|-------|---------|
| `llm-call` | Yes | Scoring — role=`product_owner`, action=`score-ambiguity`, tools disabled |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier (a new one is minted if empty) |
| `issueId` | string | Issue identifier |
| `requirement` | string | The requirement text to score |
| `context` | string | Optional supporting context findings |
| `threshold` | double | Clarify threshold (0..1; <= 0 or unset → default 0.5) |
| `tenantId` | string | Tenant id (GUID string, or empty in single-user mode) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `sessionId` | string | Session identifier |
| `status` | string | `scored` on success |
| `score` | double | Ambiguity score (0..1) |
| `ambiguityCount` | int | Number of itemised ambiguities |
| `confidence` | double | The model's confidence in the assessment (0..1) |
| `threshold` | double | The effective (resolved) threshold |
| `decision` | string | `clarify` or `proceed` |
| `assessment` | string | The serialized `AmbiguityAssessment` JSON (`{}` on failure) |

## Events Emitted

| Event | Status | When |
|-------|--------|------|
| `AMBIGUITY.STARTED` | success | Scoring begins |
| `AMBIGUITY.SCORED` | success | A valid assessment was parsed (carries score, item count, confidence; the score is nullable event data so a genuine 0.0 is recorded) |
| `AMBIGUITY.CLARIFICATION_TRIGGERED` | success | Score met/exceeded the threshold — decision=`clarify` |
| `AMBIGUITY.BELOW_THRESHOLD` | success | Score below the threshold — decision=`proceed` |
| `AMBIGUITY.FAILED` | error (LOUD) | The scoring `llm-call` failed or output was unparseable/out-of-range — never a fabricated score |

## Fail-Closed Parsing

`AmbiguityParsing.ParseAssessment` returns `null` (routing to the error terminal) on empty output, no JSON, a missing/out-of-range score, or a missing rationale. Empty-shell breakdown items are dropped; item `type` is normalized onto `vague` / `missing` / `contradictory` / `implicit` (else `unspecified`) and `severity` onto `low` / `medium` / `high`. An empty breakdown with score near 0 is a VALID result — it means "clear requirement", not a failure.

## Assessment Shape

```json
{
  "score": 0.7,
  "confidence": 0.85,
  "rationale": "Why the requirement scored this way (load-bearing)",
  "ambiguities": [
    {
      "type": "missing",
      "description": "No error-handling behaviour specified",
      "severity": "high",
      "recommendation": "Ask which failures must be retried vs surfaced"
    }
  ]
}
```

---

_See also: [Clarifying Questions](/workflows/clarifying-questions) | [Research](/workflows/research) | [LLM Call](/workflows/llm-call) | [Workflows Index](/workflows)_
