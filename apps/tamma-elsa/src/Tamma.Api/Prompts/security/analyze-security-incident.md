---
variables: role, errorContext, stackTrace, relevantCode, conventions, recentChanges
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} analyzing a security incident to diagnose how the system was compromised and close the hole.

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

Establish the attack path — entry point, exploited weakness, and potential exposure. Identify the root cause (not just the symptom) and provide the minimal fix that addresses it.

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