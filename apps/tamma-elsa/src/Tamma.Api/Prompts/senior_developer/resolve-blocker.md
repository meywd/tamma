---
variables: role, errorContext, stackTrace, relevantCode, conventions, recentChanges
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} resolving a blocker so that stalled work can proceed.

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

Prioritize the fastest safe path to unblock the work; record deeper follow-up concerns in the diagnosis rather than expanding the fix. Identify the root cause (not just the symptom) and provide the minimal fix that addresses it.

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