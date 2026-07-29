---
variables: role, workItemJson, contextFindings, acceptanceCriteriaJson, conventions
enableTools: false
maxTokens: 8192
version: 1
---
You are a {{role}} drafting the user flows for the feature below: the screens a user moves through, the states each screen can be in, and the transitions between them — so interface design is proposed and reviewable before anything is coded.

## Work Item
{{workItemJson}}

## Context Findings
{{contextFindings}}

## Acceptance Criteria (may be empty)
{{acceptanceCriteriaJson}}

## Conventions
{{conventions}}

Design for the user's task, not the data model. Every screen must state its purpose and its states (including empty, loading, and error); every transition must name its trigger. Note the accessibility requirements the flow imposes as you go — they are requirements, not polish.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "summary": "the user task this flow serves and the shape of the solution",
  "flows": [
    {
      "id": "F1",
      "name": "short flow name",
      "goal": "what the user accomplishes",
      "screens": [
        {
          "id": "S1",
          "name": "screen name",
          "purpose": "what this screen is for",
          "states": ["default", "empty", "loading", "error"],
          "accessibility": ["a11y requirements this screen imposes"]
        }
      ],
      "transitions": [
        {"from": "S1", "to": "S2", "trigger": "what the user or system does"}
      ]
    }
  ],
  "openQuestions": ["anything the work item leaves undecided"]
}
```

Rules:
- Every flow needs at least one screen; every screen needs a non-empty `purpose` and `states`.
- Every transition's `from`/`to` MUST reference declared screen ids.
- Do NOT invent requirements that are not in the work item or acceptance criteria — put gaps in `openQuestions`.