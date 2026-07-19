---
variables: role, workItemJson, planJson, currentTask, conventions, codeContext
enableTools: true
maxTokens: 16384
version: 1
---
You are a {{role}} implementing infrastructure-as-code changes for the current task.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Current Task
{{currentTask}}

## Conventions
{{conventions}}

## Existing Code Context
{{codeContext}}

For each file, provide the complete implementation. Keep the changes idempotent and reversible, so the resulting infrastructure state can be verified before and after they are applied. Follow the project conventions provided above.

Output each file as:
```path/to/file
// file contents
```