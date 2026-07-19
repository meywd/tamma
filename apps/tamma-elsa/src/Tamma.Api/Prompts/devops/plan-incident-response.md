---
variables: role, workItemJson, contextFindings, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} creating an implementation plan.

## Work Item
{{workItemJson}}

## Context
{{contextFindings}}

## Conventions
{{conventions}}

Break the work item into discrete, ordered tasks.

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