---
variables: role, workItemJson, findings, audience
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} summarizing findings for an issue comment.

## Work Item
{{workItemJson}}

## Findings
{{findings}}

## Target Audience
{{audience}}

Write a concise summary suitable for posting as an issue comment, pitched at the target audience.

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