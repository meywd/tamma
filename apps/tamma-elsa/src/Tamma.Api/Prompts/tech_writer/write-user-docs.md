---
variables: role, workItemJson, findings, audience
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} drafting end-user documentation for the work item below.

## Work Item
{{workItemJson}}

## Findings
{{findings}}

## Target Audience
{{audience}}

Write concise user-facing documentation suitable for posting as an issue comment, pitched at the target audience — explain the feature in task-oriented terms ("how do I...") and avoid internal implementation vocabulary.

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