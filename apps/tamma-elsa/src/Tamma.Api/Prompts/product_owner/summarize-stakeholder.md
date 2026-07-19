---
variables: role, workItemJson, findings, audience
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} writing a stakeholder-facing status update on the work item below.

## Work Item
{{workItemJson}}

## Findings
{{findings}}

## Target Audience
{{audience}}

Write a concise status update suitable for posting as an issue comment, pitched at the target audience — lead with outcomes, risks, and decisions needed, not implementation detail.

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