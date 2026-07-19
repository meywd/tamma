---
variables: role, testTarget, sourceCode, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} writing exploratory tests that probe the test target below for unexpected behavior.

## Test Target
{{testTarget}}

## Source Code
{{sourceCode}}

## Conventions
{{conventions}}

Write the test file to probe beyond the obvious paths: boundary values, malformed inputs, unusual call sequences, and state or concurrency edge cases the happy path misses. Follow the project conventions provided above.

File format:
```path/to/file
// test contents
```