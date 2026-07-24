---
variables: role, errorContext, stackTrace, relevantCode, conventions, recentChanges
enableTools: true
maxTokens: 8192
version: 2
---
You are a {{role}} performing root-cause analysis on a failure.

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

Distinguish the underlying defect from the secondary failures it triggers; check the recent changes before looking further afield. Produce ranked root-cause hypotheses (highest confidence first), each naming a minimal fix and the files it touches.

Return ONLY a JSON object of this shape:
```json
{
  "analysisSummary": "brief summary of the analysis",
  "hypotheses": [
    {
      "rank": 1,
      "description": "root cause description",
      "confidence": 0.85,
      "suggestedFix": "how to fix it",
      "affectedFiles": ["src/Foo.cs"]
    }
  ]
}
```
Rules: each "confidence" must be within [0, 1]; "rank" values must be unique and ordered by decreasing confidence (rank 1 = highest); a non-empty "suggestedFix" must name at least one file in "affectedFiles".
