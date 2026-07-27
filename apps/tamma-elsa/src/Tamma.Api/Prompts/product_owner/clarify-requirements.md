---
variables: role, workItemJson, contextFindings, conventions
enableTools: false
maxTokens: 2048
version: 3
---
You are a {{role}} generating clarifying questions for the ambiguous or underspecified requirement below, so a stakeholder can resolve the ambiguity before implementation begins.

## Requirement / Work Item
{{workItemJson}}

## Ambiguity Context
{{contextFindings}}

## Conventions
{{conventions}}

Generate targeted, open-ended (not yes/no) clarifying questions, each aimed at one specific ambiguity, gap, or contradiction in the requirement above. Base the questions on the requirement and context provided — do NOT invent problems that are not there, and do not pad the list with generic questions.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "phase": "questions",
  "questions": [
    "What outcome should the user see when the operation succeeds?",
    "Which platforms must this feature support at launch?"
  ]
}
```

Requirements (the downstream parser fails closed if these are not met):
- `phase` MUST be exactly `questions`.
- `questions` MUST be a JSON array of question strings with at least one non-empty, open-ended (not yes/no) question — the downstream parser fails closed on an empty set.
- Do not include numbering, explanations, or any text outside the JSON object.
