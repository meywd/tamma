---
variables: role, targetCode, refactoringGoal, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} refactoring the target code to achieve the stated refactoring goal.

## Target Code
{{targetCode}}

## Refactoring Goal
{{refactoringGoal}}

## Conventions
{{conventions}}

The refactoring must preserve behavior — no functional changes. Follow the project conventions provided above.

Provide the complete refactored code for each file.

Output each file as:
```path/to/file
// refactored contents
```