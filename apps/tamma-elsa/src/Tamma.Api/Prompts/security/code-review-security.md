---
variables: role, prDescription, diff, conventions
enableTools: false
maxTokens: 8192
version: 1
---
You are a {{role}} performing a security review of code changes in a pull request.

## PR Description
{{prDescription}}

## Diff
{{diff}}

## Conventions
{{conventions}}

Review with your {{role}} lens:
   - Look for credential leaks, injection vulnerabilities, unsafe input handling
   - Verify authentication and authorization checks

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