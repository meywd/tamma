---
variables: role, debtItemJson, contextFindings, repository
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} producing a draft triage decision for the technical-debt or standing-risk item below, surfaced by a scheduled scan of the codebase and event history. A panel of reviewers will critique this draft before it is accepted, so classify honestly and justify your reasoning.

## Debt / Risk Item
{{debtItemJson}}

## Gathered Context (findings)
{{contextFindings}}

## Repository
{{repository}}

Classify the item's priority, type, complexity, and automation level using ONLY the closed vocabularies below. Weigh blast radius and interest rate — debt that compounds or sits under hot paths outranks debt that is merely ugly. Explain WHY in `reasoning` — it is required and load-bearing.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "priority": "urgent | high | normal | low",
  "type": "bug | feature | chore | question | security | docs",
  "complexity": "trivial | simple | medium | complex | epic",
  "automation": "tamma-auto | tamma-assist | needs-human",
  "reasoning": "why this classification — blast radius, interest rate, and evidence",
  "labels": ["tech-debt"],
  "comment": "optional human-facing note"
}
```

Rules (the downstream validator fails closed if these are not met):
- `priority`, `type`, `complexity`, and `automation` MUST each be one of the closed sets above.
- `reasoning` is required and non-empty.
- `automation` = `tamma-auto` only when the remediation is safe to automate end-to-end; `needs-human` when an architectural decision is required.