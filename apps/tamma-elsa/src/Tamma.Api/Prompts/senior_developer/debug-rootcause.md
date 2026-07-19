---
variables: role, errorContext, stackTrace, relevantCode, conventions, recentChanges
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} diagnosing and fixing a failure.

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

Identify the root cause (not just the symptom) and provide the minimal fix that addresses it.

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