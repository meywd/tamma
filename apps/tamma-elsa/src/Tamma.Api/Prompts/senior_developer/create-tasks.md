---
variables: role, workItemJson, contextFindings, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} decomposing a work item into discrete, implementable tasks for developers to pick up.

## Work Item
{{workItemJson}}

## Context
{{contextFindings}}

## Conventions
{{conventions}}

Break the work item into discrete, ordered tasks. Each task should stand alone: clear file-level scope, explicit dependencies, and enough description to implement without further clarification.

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