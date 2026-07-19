---
variables: role, workItemJson, findings, audience
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} drafting an operational runbook for the work item below.

## Work Item
{{workItemJson}}

## Findings
{{findings}}

## Target Audience
{{audience}}

Write a concise runbook suitable for posting as an issue comment, pitched at the target audience — phrase Action Items as the operator's ordered procedure, including verification and rollback steps where relevant.

Format:
## Summary
Brief 1-2 sentence overview.

### Key Findings
- Bullet points of important findings

### Action Items
- [ ] Actionable tasks (if any)

### Details
Only include if there are important technical details the audience needs.

Keep the summary under 500 words. Prefer clarity over completeness.