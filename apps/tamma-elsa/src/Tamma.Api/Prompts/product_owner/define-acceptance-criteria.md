---
variables: role, workItemJson, contextFindings, conventions
enableTools: true
maxTokens: 8192
version: 2
---
You are a {{role}} defining the testable definition-of-done for the work item below: the acceptance criteria the merge gate checks against and acceptance verification replays. This is NOT a task breakdown — you are not deciding HOW the work is done, only WHEN it is done.

## Work Item
{{workItemJson}}

## Context (accepted clarification and findings, when they exist)
{{contextFindings}}

## Conventions
{{conventions}}

Write one criterion per independently testable condition. Each criterion must be checkable ON ITS OWN, by someone who did not write the code, without reading any other criterion — a criterion that can only be judged as "the system feels right" is rejected. Cover the behaviour the work item actually asks for: the happy path, the error and edge conditions the context names, and anything the clarification pinned down. Do NOT write criteria that require scope this work item does not include — if a criterion depends on unimplemented work, drop it or restate it against what IS in scope.

Prefer `given-when-then` form for behaviour and `checklist` form for observable facts that are not a state transition. Use one form per criterion and supply the fields that form needs.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "issueId": "meywd/tamma#42",
  "criteria": [
    {
      "id": "AC-1",
      "form": "given-when-then",
      "given": "a tenant that has exhausted its request quota for the current window",
      "when": "the tenant issues another request to the API",
      "then": "the API responds 429 and the response carries a Retry-After header naming the window reset",
      "verifiable": true,
      "scopeRef": "ST-1"
    },
    {
      "id": "AC-2",
      "form": "checklist",
      "statement": "the limiter's per-tenant counters reset at the top of each window, observable in the rate_limit_window_reset metric",
      "verifiable": true
    }
  ]
}
```

Rules:
- `issueId` is REQUIRED — acceptance criteria define done for exactly ONE issue. Use the issue identity from the work item above.
- Define at least one criterion. Every criterion needs a unique, referencable `id` (`AC-1`, `AC-2`, …) — duplicate ids make the merge gate's references ambiguous.
- `form` MUST be exactly one of: `given-when-then`, `checklist`.
- A `given-when-then` criterion MUST state all three of `given`, `when` and `then`, each non-empty. A partial Given/When/Then is rejected.
- A `checklist` criterion MUST carry a non-empty `statement`. An empty line verifies nothing.
- Every criterion MUST carry `"verifiable": true` — this is an attestation that the criterion can be checked independently. If you cannot honestly attest it, rewrite the criterion until you can rather than emitting an aspirational one.
- `scopeRef` is OPTIONAL: include it only to name the planned decomposition subtask id a criterion covers, and only when that subtask exists in the context above. A `scopeRef` naming scope that is not planned is rejected — an unmapped criterion is legal, a wrongly-mapped one is not.
