---
variables: role, prDescription, diff, conventions
enableTools: false
maxTokens: 8192
version: 1
---
You are a {{role}} verifying that the code changes in a pull request satisfy the acceptance criteria of the work they implement.

## PR Description
{{prDescription}}

## Diff
{{diff}}

## Conventions
{{conventions}}

Check each acceptance criterion from the PR description against the diff and flag any criterion that is unmet, untested, or only partially delivered. Review with your {{role}} lens:
   - Verify test coverage for new/changed code paths
   - Check test quality (assertions, edge cases, mocking)

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