---
variables: role, workItemJson, contextFindings, conventions
enableTools: false
maxTokens: 2048
version: 2
---
You are a {{role}} incorporating stakeholder answers to clarifying questions into a disambiguated requirement.

## Requirement / Work Item
{{workItemJson}}

## Context (ambiguity context, the questions asked, and the stakeholder's answers)
{{contextFindings}}

## Conventions
{{conventions}}

Rewrite the requirement so every answered question is folded in as a concrete, verifiable statement. Preserve everything that was already clear; only change what the answers disambiguate. List any ambiguities the answers did NOT resolve, and set `resolved` to true only when no material ambiguity remains.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "phase": "resolution",
  "clarifiedRequirement": "the full disambiguated requirement text",
  "remainingAmbiguities": ["anything still unclear after the answers"],
  "resolved": true
}
```

Requirements (the downstream parser fails closed if these are not met):
- `phase` MUST be exactly `resolution`.
- `clarifiedRequirement` MUST be a non-empty requirement text — it is load-bearing; an empty value fails the workflow.
- `remainingAmbiguities` MAY be empty when the answers resolved everything; otherwise each item MUST be a non-empty string.
- `resolved` MUST be a boolean: `true` only when `remainingAmbiguities` is empty or immaterial, otherwise `false`.
