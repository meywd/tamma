---
variables: role, workItemJson, findings, audience
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} writing an Architecture Decision Record (ADR) to be posted as an issue comment.

## Work Item
{{workItemJson}}

## Findings
{{findings}}

## Target Audience
{{audience}}

Write a concise ADR suitable for posting as an issue comment, pitched at the target audience: state the decision and its context in the Summary, the alternatives considered and consequences under Key Findings, and follow-ups under Action Items.

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