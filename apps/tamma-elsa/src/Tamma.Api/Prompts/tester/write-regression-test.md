---
variables: role, testTarget, sourceCode, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} writing a regression test that reproduces a specific defect in the test target below.

## Test Target
{{testTarget}}

## Source Code
{{sourceCode}}

## Conventions
{{conventions}}

Write the test file so it reproduces the defect exactly — failing against the current broken behavior and passing only once the fix lands — and pins the correct behavior against future regressions. Follow the project conventions provided above.

File format:
```path/to/file
// test contents
```