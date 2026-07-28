# GitOperationsTool subcommand matching is case-INsensitive — the 43-2/43-4 enum refactor spec omits this and would regress it

**Status**: 🐛 Open (latent — no shipped code is broken today; this is a defect in the Story 43-2 AC8 / Story 43-4 refactor spec that will bite when the `GitOperationsTool` HashSet is replaced)

**Severity**: Medium

**Found during**: Epic 43 catalog-core implementation (43-2/43-3 scope), 2026-07-27, while deriving `GitSubcommand` from the tool.

## Symptom / Risk

`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs:21` declares:

```csharp
private static readonly HashSet<string> AllowedSubcommands = new(StringComparer.OrdinalIgnoreCase) { … };
```

so a model-issued `"STATUS"` or `"Push"` subcommand is **accepted today**. Story 43-2 AC8 /
Story 43-4 specify replacing this HashSet with the `[Wire]`-backed `GitSubcommand` enum, and
`EnumWire<T>` parsing is deliberately **ordinal case-sensitive** ("non-canonical casing … is
rejected, not silently accepted"). Neither story mentions the comparer. A faithful
implementation of the written spec therefore silently changes tool behaviour: previously-valid
mixed-case subcommands start being rejected — in a story whose own D11 says "the permitted set
is unchanged — this is a refactor with a count pin, not a policy change".

Note the write-grade consequence too: after 43-4 resolves the subcommand into a
`git_operations.read` / `git_operations.write` gate decision, a casing-dependent parse failure
path would also be a casing-dependent *gate* path.

## Root cause

The design/story derivation recorded the 14 names but not the `StringComparer.OrdinalIgnoreCase`
constructor argument.

## Suggested resolution (for Story 43-4, NOT fixed here — out of the catalog-core lane)

Normalize the incoming subcommand with `ToLowerInvariant()` before `EnumWire<GitSubcommand>.TryParse`,
and add a test case for `"STATUS"` / `"Push"` acceptance parity against the pre-refactor behaviour.

## Where this is already flagged in code

- `apps/tamma-elsa/src/Tamma.Core/Actions/GitSubcommand.cs` — XML doc NOTE on the enum.
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/GitSubcommandParitySweepTests.cs` — the
  live-parity sweep that must be updated/retired in the same commit as the 43-4 refactor.

## Related

- Story: `docs/stories/epic-43/story-43-2/43-2-catalog-core.md` (AC8, D11)
- Story: `docs/stories/epic-43/story-43-4/` (the refactor that would trip this)
- Code: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs:21-25,78-82`
