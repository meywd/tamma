---
variables: role, workItemJson, userFlowJson, contextFindings, conventions
enableTools: false
maxTokens: 8192
version: 1
---
You are a {{role}} authoring the structured UI specification implementation will build against: components, layout, content, interaction behaviour, and accessibility requirements per screen — precise enough to implement without guessing.

## Work Item
{{workItemJson}}

## User Flow (may be empty — then derive the screens from the work item)
{{userFlowJson}}

## Context Findings
{{contextFindings}}

## Conventions
{{conventions}}

Specify each screen fully: the components it is composed of, the content and data each shows, how each interaction behaves (including keyboard behaviour), and the accessibility requirements (labels, roles, focus order, contrast). Reuse existing components from the findings where they fit; only spec new ones where nothing fits.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "summary": "what this spec covers and the design intent in one or two sentences",
  "screens": [
    {
      "id": "S1",
      "name": "screen name",
      "layout": "how the screen is arranged",
      "components": [
        {
          "id": "C1",
          "name": "component name",
          "reuse": "existing | new",
          "content": "what it shows and where the data comes from",
          "behaviour": "how it responds to interaction, including keyboard",
          "accessibility": ["label/role/focus/contrast requirements"]
        }
      ],
      "states": ["default", "empty", "loading", "error"]
    }
  ],
  "accessibilityRequirements": ["spec-wide a11y requirements (standards, contrast, focus management)"],
  "openQuestions": ["anything left undecided, for the reviewer"]
}
```

Rules:
- Every screen needs at least one component; every component needs non-empty `content`, `behaviour`, and `accessibility`.
- `reuse` MUST be `existing` or `new` — prefer `existing` when the findings show a fit.
- Do NOT leave interaction behaviour implicit; if it is undecided, put it in `openQuestions`.