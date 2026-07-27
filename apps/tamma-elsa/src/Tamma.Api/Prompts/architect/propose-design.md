---
variables: role, workItemJson, contextFindings, repository, conventions
enableTools: false
maxTokens: 4096
version: 2
---
You are a {{role}} proposing a technical design for the complex requirement below, for human review and approval.

## Requirement / Work Item
{{workItemJson}}

## Constraints
{{contextFindings}}

## Repository
{{repository}}

## Conventions
{{conventions}}

Propose a system design: a summary of the recommended approach, at least two genuine alternatives with their trade-offs, a recommendation naming the preferred alternative and why, and an evaluation of how the design satisfies (or trades away) each stated constraint. Ground the design in the repository and constraints provided — do NOT invent requirements or constraints that are not there.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "summary": "concise description of the proposed design and how it satisfies the requirement",
  "recommendation": "which alternative is recommended and why",
  "recommendedAlternativeId": "A1",
  "constraintEvaluation": "how the design meets or trades off each stated constraint",
  "alternatives": [
    {
      "id": "A1",
      "name": "short name of the recommended alternative",
      "tradeoffs": "its costs and benefits relative to the others"
    },
    {
      "id": "A2",
      "name": "short name of the second alternative",
      "tradeoffs": "its costs and benefits relative to the others"
    }
  ]
}
```

Requirements (the downstream parser fails closed if these are not met):
- `summary` MUST be a non-empty description of the design — it is load-bearing; an empty value fails the workflow.
- `alternatives` MUST contain at least two entries, each with a unique `id` and a non-empty `name` and `tradeoffs`.
- `recommendedAlternativeId` MUST be the `id` of one of the listed alternatives — the recommendation must reference an alternative that actually appears in the list.
- `recommendation` and `constraintEvaluation` MUST be non-empty strings.
