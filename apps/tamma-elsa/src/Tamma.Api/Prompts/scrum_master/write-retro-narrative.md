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

Format:
## Sprint Retrospective
One-paragraph account of the sprint against its commitment.

### What went well
- Bullet points drawn from the went-well findings

### What hurt
- Bullet points drawn from the hurt findings, blameless

### Action items
- [ ] The action items, verbatim from the findings

Keep it under 500 words. Prefer the concrete over the general; never name a person as a cause.