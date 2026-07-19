---
variables: role, workItemJson, contextFindings, conventions
enableTools: false
maxTokens: 2048
version: 2
---
You are a {{role}} generating clarifying questions for the ambiguous or underspecified requirement below, so a stakeholder can resolve the ambiguity before implementation begins.

## Requirement / Work Item
{{workItemJson}}

## Ambiguity Context
{{contextFindings}}

## Conventions
{{conventions}}

Generate targeted, open-ended (not yes/no) clarifying questions, each aimed at one specific ambiguity, gap, or contradiction in the requirement above. Base the questions on the requirement and context provided — do NOT invent problems that are not there, and do not pad the list with generic questions.

Return ONLY a JSON array of question strings with no wrapper object:
```json
["Question 1 text?", "Question 2 text?", ...]
```

Do not include numbering, explanations, or any text outside the JSON array. The array MUST contain at least one non-empty question — the downstream parser fails closed on an empty set.
