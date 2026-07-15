---
title: "Workflow: Clarifying Questions"
---

**Definition ID:** `clarifying-questions`
**Class:** `ClarifyingQuestionsWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ClarifyingQuestionsWorkflow.cs`

## Purpose

The Clarifying Questions workflow (Story 3.5) resolves requirement ambiguity with a human in the loop. Given an ambiguous issue/requirement it generates clarifying questions via the MEDIATED `llm-call` path (role=`product_owner`, action=`clarify-requirements`; the engine holds no LLM credential), DELIVERS them to the issue via the mediated git seam, SUSPENDS on a bookmark awaiting the human answers (with a durable SLA timeout), then RESUMES via the secure resume endpoint and incorporates the answers — a second `llm-call` — into a disambiguated requirement. Every transition is emitted as a `CLARIFY.*` DCB event.

It reuses the [Assessment](/workflows/assessment) skeleton (llm-call → deliver → bookmark-wait → analyze/resume, fail-closed gates + error terminal), and is the clarification target that [Ambiguity Scoring](/workflows/ambiguity-scoring) routes to (a parent flow dispatches this workflow on the `decision="clarify"` output).

## Flow Diagram

```
+------------------+
| Read Inputs      |
| (requirement,    |
|  ambiguity       |
|  context)        |
+--------+---------+
         |
         v
+------------------+
| Generate         |
| Clarifying       |
| Questions        |
| (llm-call:       |
|  product_owner/  |
|  clarify-        |
|  requirements)   |
+--------+---------+
         |
         v
+------------------+
| Parse Questions  |
| (fail-closed)    |
+--------+---------+
         |
         v
+------------------+
| Questions OK?    |
+--+------------+--+
  YES            NO
   |              |
   v              v
+----------+ +------------------+
| Emit     | | Emit CLARIFY.    |
| CLARIFY. | | QUESTIONS.FAILED |
| QUESTIONS| | (LOUD)           |
| GENERATED| +--------+---------+
+----+-----+          |
     |                v
     v         +------------------+
+----------+   | LLM Call Error   |
| Deliver  |   | (Finish)         |
| Questions|   +------------------+
| (git     |
|  seam)   |
+----+-----+
     |
     v
+------------------+
| Emit CLARIFY.    |
| QUESTIONS.       |
| DELIVERED        |
+--------+---------+
         |
         v
+------------------+
| Wait For Answers |
| (bookmark +      |
|  durable SLA)    |
+--+------------+--+
Answered       Timeout
   |              |
   v              v
+----------+ +------------------+
| Store    | | Set Timeout      |
| Answers  | | Result           |
+----+-----+ +--------+---------+
     |                |
     v                v
+----------+ +------------------+
| Emit     | | Emit CLARIFY.    |
| ANSWERS. | | ANSWERS.TIMED_OUT|
| RECEIVED | | (LOUD)           |
+----+-----+ +--------+---------+
     |                |
     v                v
+----------+ +------------------+
| Incorpo- | | Expose Timeout   |
| rate     | | Output           |
| Answers  | +------------------+
| (llm-    |
|  call)   |
+----+-----+
     |
     v
+------------------+
| Incorporation OK?|
+--+------------+--+
  YES            NO
   |              |
   v              v
+----------+ +------------------+
| Emit     | | Emit CLARIFY.    |
| REQUIRE- | | INCORPORATION.   |
| MENTS.   | | FAILED (LOUD)    |
| CLARIFIED| +--------+---------+
+----+-----+          |
     |                v
     v         +------------------+
+----------+   | LLM Call Error   |
| Expose   |   | (Finish)         |
| Output   |   +------------------+
+----------+
```

## Bookmark Points

| Bookmark | Activity | Waits For | Outcomes |
|----------|----------|-----------|----------|
| `clarify-answers-{tenant}-{session}` | `WaitForClarifyingAnswersActivity` | Human answers via the secure resume endpoint, or the durable answer SLA (`Clarify:AnswerTimeoutMinutes`, default 4320 = 3 days) | `Answered`, `Timeout` |

The SLA is a durable `DelayFor` bookmark (EF-persisted, re-armed by `Elsa.Scheduling` after a host restart), so an unanswered question set terminates as a real `Timeout` even across restarts.

## Secure Resume

- Public API: `POST /api/adl/clarify/resume` (`WorkflowsManage` policy — SaaS member-role users get 403), which forwards to the engine's `ClarifyResumeEndpoint`.
- One canonical bookmark-name builder is shared by the suspend and resume sides; the tenant id is server-derived and folded into the bookmark name, so a cross-tenant resume attempt 404s (no IDOR). 0 matching bookmarks → 404; more than 1 → 409.

## Sub-Workflows Dispatched

| Workflow | Wait? | Purpose |
|----------|-------|---------|
| `llm-call` | Yes | Question generation — role=`product_owner`, action=`clarify-requirements`, tools disabled |
| `llm-call` | Yes | Answer incorporation — same role/action, folding the human answers into a disambiguated requirement |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier (a new one is minted if empty) |
| `issueId` | string | Issue identifier |
| `requirement` | string | The ambiguous requirement text |
| `repository` | string | Repository slug (owner/repo) for the issue-comment delivery |
| `issueNumber` | int | Issue number for the issue-comment delivery |
| `ambiguityContext` | string | Optional ambiguity context (e.g. the scoring breakdown) |
| `tenantId` | string | Tenant id (GUID string, or empty in single-user mode) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `sessionId` | string | Session identifier |
| `status` | string | `clarified` or `timed_out` |
| `clarifiedRequirement` | string | The disambiguated requirement JSON (success path) |
| `resolved` | bool | Whether the ambiguity was resolved |

## Events Emitted

| Event | Status | When |
|-------|--------|------|
| `CLARIFY.QUESTIONS.GENERATED` | success | A non-empty question set was parsed |
| `CLARIFY.QUESTIONS.DELIVERED` | success | Questions delivered to the issue |
| `CLARIFY.ANSWERS.RECEIVED` | success | Human answers arrived via the resume endpoint |
| `CLARIFY.REQUIREMENTS.CLARIFIED` | success | Answers incorporated into a disambiguated requirement |
| `CLARIFY.QUESTIONS.FAILED` | error (LOUD) | Question-generation `llm-call` failed or output was unparseable |
| `CLARIFY.INCORPORATION.FAILED` | error (LOUD) | Incorporation `llm-call` failed or output was unparseable |
| `CLARIFY.ANSWERS.TIMED_OUT` | error (LOUD) | Answer SLA expired with no response |

## Delivery Channels

`DeliverClarifyingQuestionsActivity` posts the questions as an issue comment via the mediated git seam (`PATCH /api/v1/git/{repo}/issues/{n}`; the per-tenant git token is resolved API-side — the engine holds no git credential). When no issue coordinates are supplied it falls back to `api` mode (the questions are already durable in workflow state).

## Fail-Closed Parsing

`ClarifyParsing` mirrors the other Epic-3 parsers: `ParseQuestions` yields an empty list (→ error terminal) when nothing parseable is found; `ParseClarification` returns `null` when the clarified-requirement field is missing/empty. The workflow never proceeds with fabricated questions or a fabricated clarification.

---

_See also: [Ambiguity Scoring](/workflows/ambiguity-scoring) | [Design Proposal](/workflows/design-proposal) | [Assessment](/workflows/assessment) | [LLM Call](/workflows/llm-call) | [Workflows Index](/workflows)_
