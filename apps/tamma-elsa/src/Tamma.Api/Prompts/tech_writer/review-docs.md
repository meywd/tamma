---
variables: role, prDescription, diff, conventions
enableTools: false
maxTokens: 8192
version: 1
---
You are a {{role}} reviewing a pull request for its documentation impact.

## PR Description
{{prDescription}}

## Diff
{{diff}}

## Conventions
{{conventions}}

Flag public behavior, APIs, or configuration changed in the diff without corresponding documentation updates, and any existing doc text or comments the diff makes stale or inaccurate. Review with your {{role}} lens:
   - Apply your role-specific expertise to the diff

If no issues are found, explicitly state "No issues found" with a brief explanation of what you verified.

Output as JSON:
```json
{
  "issues": [
    {
      "file": "...",
      "line": "...",
      "severity": "critical|major|minor|style",
      "category": "bug|security|performance|convention|test-coverage",
      "issue": "...",
      "fix": "..."
    }
  ],
  "summary": {
    "decision": "APPROVE|REQUEST_CHANGES|COMMENT",
    "text": "...",
    "filesReviewed": 0,
    "issuesBySeverity": {"critical": 0, "major": 0, "minor": 0, "style": 0}
  }
}
```