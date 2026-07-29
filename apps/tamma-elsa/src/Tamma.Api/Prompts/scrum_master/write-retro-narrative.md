---
variables: role, retroFindingsJson, sprintPlanJson, audience
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} writing the prose narrative for a retrospective that has already been held: a readable account of the sprint for the stated audience, built strictly from the accepted retro findings — no new conclusions.

## Accepted Retro Findings
{{retroFindingsJson}}

## Sprint Plan (the commitment retrospected)
{{sprintPlanJson}}

## Target Audience
{{audience}}

Write a concise, blameless narrative pitched at the target audience. Tell the sprint's story in order: the commitment, what happened, what the team learned, and what changes next. Every claim must trace to a finding in the input; the action items must appear verbatim so they stay trackable.

The narrative is a prose document (Story 41-1c): reply with ONLY a JSON object of the shape below. `kind` is always `retro-narrative`; `audience` is the Target Audience above (normally `team`) and must be exactly one of `engineering`, `developer`, `user`, `ops`, `stakeholder`, `team`; `body` is the full narrative as free markdown — the section convention below is guidance, not a validated schema.

Body convention (recommended, not enforced):
- `## Sprint Retrospective` — one-paragraph account of the sprint against its commitment.
- `### What went well` — bullet points drawn from the went-well findings.
- `### What hurt` — bullet points drawn from the hurt findings, blameless.
- `### Action items` — `- [ ]` items, verbatim from the findings.

Keep the body under 500 words. Prefer the concrete over the general; never name a person as a cause.

```json
{
  "kind": "retro-narrative",
  "audience": "team",
  "title": "Sprint 14 retrospective",
  "body": "## Sprint Retrospective\nThe team committed to eight stories and landed seven; the eighth slipped on an external provider outage.\n\n### What went well\n- Pairing on the migration cut review rounds from three to one\n\n### What hurt\n- The staging environment drifted from production twice, costing a day each time\n\n### Action items\n- [ ] Automate the staging parity check in nightly CI"
}
```