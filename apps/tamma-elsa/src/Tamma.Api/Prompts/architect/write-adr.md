---
variables: role, workItemJson, findings, audience
enableTools: false
maxTokens: 2048
version: 2
---
You are a {{role}} writing an Architecture Decision Record (ADR): the durable record of ONE significant technical decision — what was decided, why, what it costs, and what was rejected — so the reasoning survives the people who made it.

## Work Item
{{workItemJson}}

## Decision Context (accepted design and findings, when they exist)
{{findings}}

## Target Audience
{{audience}}

Record the decision that was actually made, from the evidence above. State it in the past tense and in the active voice ("we chose X"), not as a proposal. Name the alternatives that were genuinely considered and say plainly why each was not chosen — an ADR with no rejected alternative is a summary, not a decision record. Name the consequences you accept, including the bad ones; an ADR that lists only benefits is not trusted. Do NOT invent context, alternatives or consequences that the inputs do not support.

The ADR is a prose document (Story 41-1c): reply with ONLY a JSON object of the shape below. `kind` is always `adr`; `audience` is the Target Audience above and must be exactly one of `engineering`, `developer`, `user`, `ops`, `stakeholder`, `team` (normally `engineering`); `body` is the full ADR as free markdown — the section convention below is guidance, not a validated schema.

Body convention (recommended, not enforced):
- `## Context` — the forces at play: the problem, the constraints, and what the evidence above establishes.
- `## Decision` — the decision itself, in one or two sentences, in the past tense.
- `## Alternatives considered` — each alternative and the concrete reason it was rejected.
- `## Consequences` — what this makes easier, what it makes harder, and what it commits us to.

Keep the body under 500 words. Prefer the specific over the general; a consequence nobody would argue with is not worth writing down.

```json
{
  "kind": "adr",
  "audience": "engineering",
  "title": "ADR: scope the prose lifecycle by producer, not by document type",
  "body": "## Context\nSeven Epic 41 workflows produce prose for the same issue, and the store's latest-accepted read scopes by (issueId, documentType) with no producer filter — so a roadmap and an ADR written for one issue are indistinguishable to a re-entry.\n\n## Decision\nWe scoped each prose binding's lifecycle on a producer-suffixed issue id (`{issueId}#adr`), reusing the mechanism the two plan producers already use.\n\n## Alternatives considered\n- A distinct document type per prose kind: rejected — ten kinds would mean ten registrations and ten validators for one unvalidated body.\n- A `kind` filter on the store read: the right long-term fix, but it changes a shared read path that six landed workflows depend on, so it is filed rather than taken here.\n\n## Consequences\nEach prose producer's re-entry and latest-accepted slice is isolated with no schema change. The cost is that a consumer wanting \"the ADR for this issue\" must know the scope suffix, and the underlying store gap stays open until the filter lands."
}
```

Rules:
- `kind` MUST be exactly `adr`.
- `audience` MUST be exactly one value from the closed set above, lowercase as written. An unknown audience is rejected — it is not normalised to a default.
- `title` is REQUIRED and non-empty. Name the decision, not the issue.
- `body` is REQUIRED and must not be empty or whitespace-only. Its CONTENT is unvalidated markdown: headings, ordering and structure are yours, and the convention above is a convention.
- Return the JSON object and nothing else — no markdown fence around it, no commentary before or after.
