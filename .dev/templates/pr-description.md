## Story
Closes #<issue-id> — implements Story <epic>-<story>.

## Summary
<1-3 sentences>

## Layer
Layer <N> / Team <letter>

## Migration
<number(s)> or "none"

## Depends on
<list of merged PRs this depends on>

## Test Plan
- [ ] Unit tests added/updated
- [ ] Integration tests pass on shared test DB
- [ ] Coverage >= 80% line
- [ ] `pnpm build` passes
- [ ] `pnpm lint` passes

## Deploy Requirement
none | docker-redeploy | nginx-config-change | env-var-addition

## Reviewers
- [ ] Team reviewer
- [ ] Cross-team reviewer
- [ ] Migration steward (if migration added)
- [ ] Deploy coordinator (if deploy required)
