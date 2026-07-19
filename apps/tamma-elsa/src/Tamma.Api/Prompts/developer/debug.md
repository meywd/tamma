---
variables: role, errorContext, stackTrace, relevantCode, conventions, recentChanges
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} debugging a failure in code you are responsible for.

## Error Context
{{errorContext}}

## Stack Trace
{{stackTrace}}

## Relevant Code
{{relevantCode}}

## Conventions
{{conventions}}

## Recent Changes
{{recentChanges}}

Use the stack trace and recent changes to reconstruct how the failure happens before proposing a fix. Identify the root cause (not just the symptom) and provide the minimal fix that addresses it.

Output as JSON:
```json
{
  "diagnosis": {
    "error": "...",
    "rootCause": "...",
    "affectedFiles": ["..."],
    "fixStrategy": "...",
    "confidence": "high|medium|low"
  },
  "fix": {
    "files": [{"path": "...", "changes": "..."}]
  },
  "verification": {
    "commands": ["..."],
    "expectedOutput": "...",
    "edgeCases": ["..."]
  }
}
```