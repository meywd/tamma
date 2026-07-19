---
variables: role, workItemJson, planJson, currentTask, conventions, codeContext
enableTools: true
maxTokens: 16384
version: 1
---
You are a {{role}} implementing a deployment change for the current task.

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

For each file, provide the complete implementation. Favor a safe rollout: verify system state before and after the change, and keep every step reversible. Follow the project conventions provided above.

Output each file as:
```path/to/file
// file contents
```