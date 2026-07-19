---
variables: role, workItemJson, contextFindings, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} designing the implementation plan for the work item below.

## Work Item
{{workItemJson}}

## Context
{{contextFindings}}

## Conventions
{{conventions}}

Break the work item into discrete, ordered tasks. Settle the significant design decisions in the plan itself so the implementer is not left choosing an approach mid-task.

Output as JSON:
```json
{
  "tasks": [
    {
      "id": "T1",
      "description": "...",
      "files": [{"path": "...", "action": "create|modify"}],
      "dependencies": [],
      "complexity": "small|medium|large",
      "testing": "..."
    }
  ],
  "totalComplexity": "small|medium|large",
  "estimatedDuration": "..."
}
```