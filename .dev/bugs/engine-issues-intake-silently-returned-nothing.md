# Bug: the real issue-intake path silently returned "nothing found", always

**Date**: 2026-07-25
**Status**: ✅ Fixed
**Severity**: 🔴 High — the non-mock intake path never produced a candidate
**Found by**: the Epic 44 survey (native work tracker)

## Summary

`GET /api/engine/issues` returns an **object**:

```csharp
// EngineEndpoints.GetIssues
return ToHttpResult(result, r => Results.Ok(new { issues = r.Issues, total = r.Total }));
```

All three engine-side call sites deserialized that body into a **`List<WorkItem>`**:

| Call site | What it did |
|---|---|
| `SelectWorkItemActivity.cs:186` (auto-labelled issues) | `Deserialize<List<WorkItem>>` |
| `SelectWorkItemActivity.cs:220` (untriaged count) | `Deserialize<List<WorkItem>>` |
| `FetchUntriagedItemsActivity.cs:92` | `Deserialize<List<WorkItem>>` |

Deserializing an object as an array throws `JsonException`. In
`SelectWorkItemActivity.FetchCandidates` the whole fetch is wrapped in

```csharp
catch (Exception ex) { Logger?.LogError(ex, "Error fetching candidates from engine"); }
```

so the throw became one log line and the method returned an empty candidate list with
`untriaged = 0`. `RunAsync` then took the `candidates.Count == 0 && untriaged == 0`
branch and completed with **`NothingFound`** — "no work items found, repo is clean".

**So with `Anthropic:UseMock = false`, the autonomous loop could never select an issue.**
It reported a clean repo on every run, at HTTP 200, with no exception surfacing anywhere.

## Why nothing caught it

- **The mock path works.** `SimulateCandidates()` returns items directly, so every
  demo, and any environment with `UseMock = true`, behaves correctly.
- **The failure is indistinguishable from the success case.** "No issues to work on" is
  a legitimate, expected outcome — there is no alarm to raise.
- **Neither activity had a unit test.** Confirmed across
  `tests/Tamma.Activities.Tests/ADL/`.
- **No contract test binds the endpoint's response shape to its consumers.** They are
  in different assemblies and communicate over HTTP, so the compiler cannot help.

## Fix

A named `EngineIssuesResponse` envelope (`{ issues, total }`) in
`SelectWorkItemActivity.cs`, and all three call sites deserialize it and take
`.Issues`. Keeping it as a named type — rather than inlining an anonymous shape at each
site — is what stops the next caller repeating the mistake.

`tests/Tamma.Activities.Tests/ADL/EngineIssuesResponseTests.cs` pins the wire contract
in both directions: the envelope parses the real body, **and the old
`List<WorkItem>` call still throws against it**. If that second test ever stops
throwing, the endpoint has changed shape and the envelope is now wrong.

## Lesson

**A broad `catch (Exception)` around an I/O block turns a contract bug into a business
outcome.** The swallow is what made this survive — a `JsonException` reaching the
workflow would have failed the run loudly on day one and been fixed in minutes.

Where a catch that broad is genuinely wanted (a hosted service that must not take down
the host, a best-effort audit emit), the deserialization should sit **outside** it, or
the catch should re-throw the deserialization case specifically. "Fetch failed" and
"fetch succeeded and returned nothing" must not be the same value.

Second lesson: **a mock path that works is not evidence the real path does.** The two
diverged here for as long as the code has existed.

## Related

- `apps/tamma-elsa/src/Tamma.Activities/ADL/SelectWorkItemActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs`
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs` (`GetIssues`)
- `docs/stories/epic-44/story-44-7/` (owns the intake seam; this fix lands ahead of it)
