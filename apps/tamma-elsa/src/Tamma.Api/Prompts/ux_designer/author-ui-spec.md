---
variables: role, workItemJson, userFlowJson, contextFindings, conventions
enableTools: false
maxTokens: 8192
version: 2
---
You are a {{role}} authoring the structured UX specification implementation will build against: the user flows with their entry, success, and error states, the screens each flow passes through, and the accessibility requirements per screen — precise enough to implement without guessing.

## Work Item
{{workItemJson}}

## User Flow (may be empty — then derive the flows from the work item)
{{userFlowJson}}

## Context Findings
{{contextFindings}}

## Conventions
{{conventions}}

Specify each flow fully: where the user starts (`entryState`), what done looks like (`successState`), and at least one designed failure path (`errorStates`) — a flow that cannot fail is a flow that was not designed. Bind every screen to the flow it belongs to and state its accessibility requirements explicitly (labels, roles, focus order, contrast, keyboard behaviour). Where the work item carries acceptance criteria, map each flow to the criteria it satisfies via `acceptanceCriteriaRefs`. Do NOT leave interaction behaviour implicit — undecided behaviour belongs in a named error state or an explicit requirement, never in a gap.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "flows": [
    {
      "id": "F1",
      "name": "the user flow",
      "entryState": "where the user starts",
      "successState": "what done looks like",
      "errorStates": ["at least one designed failure path"],
      "acceptanceCriteriaRefs": ["AC-1"]
    }
  ],
  "screens": [
    {
      "id": "S1",
      "flowRef": "F1",
      "a11yRequirements": ["all inputs labelled for screen readers", "focus order follows the visual order"]
    }
  ]
}
```

Rules:
- Define at least one flow; every flow MUST state an `entryState`, a `successState`, and at least one entry in `errorStates`.
- Every screen MUST reference a declared flow via `flowRef` and list at least one entry in `a11yRequirements` — no screen ships without stated accessibility.
- `acceptanceCriteriaRefs` maps each flow to the acceptance criteria it satisfies; leave it empty only when the work item carries no criteria.
