# Bug: Nondeterministic TestCaseSource display names make `dotnet test` totals unstable

**Date Discovered**: 2026-07-27
**Reporter**: Claude (Story 44-0 implementation)
**Severity**: 🟢 Low
**Status**: 🐛 Open

## 📋 Summary

Running `dotnet test tests/Tamma.Core.Tests` repeatedly on an **unchanged** tree reports a
different `Total` each time (observed consecutive runs: 544 → 562 → 579 → 585 → 595, all
passing). Filtered runs are stable, so no tests are actually flaky — the *count* is. Root
cause: `[TestCaseSource]` case factories that embed fresh `Guid.NewGuid()` values and
`DateTimeOffset.UtcNow` timestamps in the constructed argument objects, so every NUnit
test-case **display name** changes between discovery and execution. VSTest merges/undercounts
cases whose names shift, producing unstable totals.

Found while establishing before/after test counts for Story 44-0 (an out-of-lane
observation; not caused by and not fixed in that story).

## 🔍 Details

### Affected Components

- File: `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Channels/ChannelMessageContractTests.cs:38`
  (`[TestCaseSource(nameof(Kinds))]` — case objects carry `Guid.NewGuid()` ids,
  `DateTimeOffset` timestamps, and full record `ToString()` bodies in the display name; a
  `--list-tests` diff of two consecutive discoveries shows the same logical cases with
  different names each time)
- Any other `TestCaseSource` following the same pattern (e.g. equivalents in
  `Tamma.Activities.Tests` — its totals move between runs the same way)

### Reproducibility

- [x] Always reproducible

## 🔬 Steps to Reproduce

```bash
cd apps/tamma-elsa
dotnet test tests/Tamma.Core.Tests --nologo -v q | tail -1
dotnet test tests/Tamma.Core.Tests --nologo -v q | tail -1
# Totals differ run-to-run with zero code changes.

# Contrast — stable when filtered:
dotnet test tests/Tamma.Core.Tests --filter "FullyQualifiedName~Tests.Tracking"   # stable
dotnet test tests/Tamma.Core.Tests --filter "FullyQualifiedName!~Tests.Tracking"  # stable

# See the shifting names:
dotnet test tests/Tamma.Core.Tests --list-tests > /tmp/a
dotnet test tests/Tamma.Core.Tests --list-tests > /tmp/b
diff /tmp/a /tmp/b   # same cases, different GUIDs/timestamps in display names
```

## 💥 Impact

- "Test counts before/after" verification (used in every story's DoD) cannot be read off an
  unfiltered run; totals move by ±tens with no code change.
- CI totals are not comparable across runs; a silently *lost* (merged/undercounted) test case
  is indistinguishable from count noise.

## 💡 Suggested Fix (not applied — outside Story 44-0's lane)

Give each `TestCaseSource` case a stable name via `TestCaseData.SetName(...)` (or
`SetArgDisplayNames`), and construct case objects with fixed ids/timestamps rather than
`Guid.NewGuid()` / `UtcNow` so the display name is deterministic. The cases themselves are
sound; only their identity is unstable.

## Related

- Story: `docs/stories/epic-44/story-44-0/` (discovery context only)
- File: `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Channels/ChannelMessageContractTests.cs`
