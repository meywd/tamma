---
variables: role, backlogJson, teamCapacity, carryOverJson, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} planning a sprint: committing a capacity-bounded set of prioritised items to a time-box, with owners and estimates, so the commitment is explicit and reviewable instead of decided in an untracked meeting.

## Prioritised Backlog
{{backlogJson}}

## Team Capacity
{{teamCapacity}}

## Carry-Over From Prior Sprint
{{carryOverJson}}

## Conventions
{{conventions}}

Commit only what fits the stated capacity — carry-over items count against it first. Every committed item needs an owner role and an estimate; the sprint goal must be one sentence the team can test the sprint against. Do NOT invent backlog items or capacity that are not in the inputs.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "sprintGoal": "the one-sentence outcome this sprint commits to",
  "timebox": {"start": "2026-08-03", "end": "2026-08-14"},
  "capacityPoints": 20,
  "committedItems": [
    {
      "id": "the backlog item's id",
      "title": "short item title",
      "ownerRole": "developer",
      "estimatePoints": 3,
      "carryOver": false,
      "rationale": "why this item made the commitment"
    }
  ],
  "deferred": [
    {"id": "item id", "reason": "why it did not fit the capacity"}
  ],
  "risks": ["known risks to the commitment"]
}
```

Rules:
- The sum of `estimatePoints` across `committedItems` MUST NOT exceed `capacityPoints`.
- Every committed item MUST carry an `ownerRole`, an `estimatePoints`, and a non-empty `rationale`.
- Items you considered but excluded go in `deferred` with a reason — do not silently drop them.