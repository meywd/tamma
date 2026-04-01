---
title: "Epic 14: Custom ELSA Studio"
---

## Overview

**Goal**: Replace the upstream `elsa-studio-v3-5` Docker image with a custom Blazor WASM project that references ELSA Studio NuGet packages, enabling Tamma branding, custom theme, custom UI hint handlers, and future extensibility.

**Value Delivered**:
- Tamma-branded Studio (logo, colors, favicon, app title)
- Purple-themed MudBlazor palette matching Tamma design language
- Custom Dockerfile producing a ~30MB nginx-served static site
- CI/CD integration for automated builds and pushes to GHCR
- JSON editor UI hint for workflow JSON inputs (replaces plain text fields)
- Provider selector UI hint for multi-select provider configuration

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 14.1 | Studio Blazor WASM Scaffold | P0 | None | Planned |
| 14.2 | Studio Docker & CI | P1 | Story 14.1 | Planned |
| 14.3 | Studio Custom UI Hints | P2 | Story 14.1 | Planned |

## Architecture

New project: `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj` (Blazor WASM, net8.0). References ELSA Studio NuGet packages (all pinned to 3.5.3). Deployed as static files served by nginx in a Docker container.

## Source Plan

`.dev/plans/elsa-studio-customization.md`

---

**Last Updated**: 2026-03-28
**Epic Owner**: Platform Team
