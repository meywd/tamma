# Finding: Docker Hub timeouts are a recurring CI flake on the image-build jobs

**Date**: 2026-08-03
**Severity**: medium (wastes a full CI round per hit; two hits in two days)

## Symptom

`Build tamma-api` / `Build Dashboard-User` fail before any code compiles:

```
#1 pulling image moby/buildkit:buildx-stable-1
#1 ERROR: ... Get "https://registry-1.docker.io/v2/": Client.Timeout exceeded
```

Occurrences: 2026-08-02 (job 91525646766, Dashboard-User), 2026-08-03 (job
91627268706, tamma-api — BOTH the first attempt and the workflow's own
sleep-60 retry hit the identical timeout, so one in-job retry is not enough).

## Root cause

`docker/setup-buildx-action` with the `docker-container` driver must pull
`moby/buildkit:buildx-stable-1` from Docker Hub at job start. Docker Hub
rate-limits/times out anonymously from Azure runner IP ranges.

## Fix options (needs an owner; CI workflow edit)

1. Use `driver: docker` for jobs that do not need multi-platform/cache
   exports — no buildkit image pull at all. Cheapest.
2. Or pin the buildkit image to a mirror:
   `driver-opts: image=public.ecr.aws/vend/moby/buildkit:buildx-stable-1`
   (or a GHCR mirror), avoiding registry-1.docker.io.
3. Or authenticate the pull (docker/login-action with a Hub token) to lift
   the anonymous rate limit.

## Interim

Re-run the job; the rerun-failed-jobs API returns 403 for this session's
token, so the retrigger is an empty-ish commit. This finding's commit is one.
