# Finding NNN: [Concise title — noun phrase, not a sentence]

**Scope**: [auth | orgs | providers | prompts | engine | github | kb | admin-db]
**Severity**: P0 (cutover-blocking) | P1 (feature broken) | P2 (correctness/observability) | P3 (drift/contract)
**Status**: Not-yet-implemented (stub) | Incomplete (partial port, missing N behaviors) | Behavioral drift (ported but semantics diverged) | Semantic rewrite (structure changed, not a port) | Data-model regression
**Estimated port effort**: Xh

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:<path>`.

- File: `packages/api/src/<path>:<line>`
- Contract/behavior: [describe the function, endpoint, or data model]
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/... (9e9a57c~1)
[paste the relevant 5-40 lines]
```

- Dependencies: [other TS modules this relies on]
- Tests that exercised this: [packages/api/src/__tests__/... references]

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/<path>:<line>`
- Contract/behavior: [describe what's there now — honest about stubs]
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/... (current)
[paste the relevant 5-40 lines]
```

- Dependencies: [other C# files, services, DI registrations]
- Tests: [list of Tamma.Api.Tests files hitting this + whether they'd catch the gap]

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: [specific action/output/side-effect]
- C# does: [specific action/output/side-effect]
- For a caller sending [specific input], TS returns [X] and C# returns [Y].
- In production with existing data / deployed clients, this means: [observable consequence].

Error paths:
- TS error path: [code, status, body]
- C# error path: [code, status, body]

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-X/Y-Z-slug.md`
- Story's acceptance criteria for this behavior: [quote the AC bullets]
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior (neither TS nor C# matches the story)
  - [ ] No story — spec gap; must be backfilled before remediation

If no story: what CLAUDE.md or architecture.md section governs this?

## 5. Status

- **Classification**: [one of: Not-yet-implemented (stub) / Incomplete / Behavioral drift / Semantic rewrite / Data-model regression]
- **What's needed to finish**:
  1. [specific step]
  2. [specific step]
- **Is it "just a stub" or is scope missing?** [be explicit about whether the scope was understood and not implemented, vs whether the scope itself was never spec'd]
- **Blockers**: [e.g. depends on finding #NN, requires schema change, requires coordinating with deployed engines]

## Remediation

- Files to modify: [list]
- Files to create: [list]
- Tests to add: [list specific test cases, not just "add tests"]
- Estimated effort: Xh broken down as:
  - Change A: Yh
  - Change B: Zh

## References

- TS source: `packages/api/src/...` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/...`
- Story: `docs/stories/epic-X/Y-Z-slug.md` (if exists)
- Related findings: `docs/audit/port-gaps/<scope>/NNN-*.md` (cross-refs)
- CLAUDE.md section: [if relevant]
- Archived SQL migration: `database/archived-sql-migrations/NNN-*.sql` (if schema-related)
