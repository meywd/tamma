---
variables: role, workItemJson, contextFindings, acceptanceCriteriaJson, repository, conventions
enableTools: false
maxTokens: 8192
version: 1
---
You are a {{role}} authoring the full system-design document for the larger feature below — API contract, data model, and integration points, with genuinely weighed alternatives — so the design is proposed, reviewed, and accepted before implementation planning, instead of improvised in the plan step.

## Requirement / Work Item
{{workItemJson}}

## Context Findings
{{contextFindings}}

## Acceptance Criteria (may be empty)
{{acceptanceCriteriaJson}}

## Repository
{{repository}}

## Conventions
{{conventions}}

Cover every surface the feature touches: the API contract it exposes or changes, the data model it persists, and the integration points with existing components. Present at least two real alternatives with their trade-offs and recommend one. Ground everything in the repository and findings — do NOT invent constraints or requirements that are not there.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "summary": "the recommended design in one or two sentences, covering API, data model, and integration",
  "recommendation": "which alternative is recommended and why it wins the trade-offs",
  "recommendedAlternativeId": "A1",
  "constraintEvaluation": "how the design meets or trades off each stated constraint and acceptance criterion",
  "alternatives": [
    {
      "id": "A1",
      "name": "short name of the recommended alternative",
      "tradeoffs": "its costs and benefits across API, data model, and integration"
    },
    {
      "id": "A2",
      "name": "short name of the second alternative",
      "tradeoffs": "its costs and benefits relative to the others"
    }
  ]
}
```

Rules (the downstream validator fails closed if these are not met):
- `summary`, `recommendation`, and `constraintEvaluation` MUST be non-empty.
- `alternatives` MUST contain at least two entries, each with a unique `id` and non-empty `name` and `tradeoffs`.
- `recommendedAlternativeId` MUST match the `id` of one listed alternative.