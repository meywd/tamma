# Tamma Wiki Pages

This directory contains the source markdown files for the Tamma GitHub Wiki.

## Pages

### General

- **Home.md** - Wiki homepage with quick links, project overview, and status
- **Epics.md** - Index of all 26 epics with links
- **Roadmap.md** - Project roadmap with all 26 epics and timeline
- **Architecture.md** - System architecture (dual TypeScript + C#/ELSA stack)
- **Installation.md** - Installation & setup: Docker Compose stack, `.env`, health checks, VPS/qa-tag deploy (Story 5-9a)
- **Usage-and-Configuration.md** - CLI commands, operating modes, `.tamma/config.json`, prompt store, providers, BYOK (Story 5-9b)
- **API-Reference.md** - REST surface, RBAC policies, SSE streams, webhooks, DCB event catalog (Story 5-9c)
- **Event-Schema-and-Catalog.md** - Epic 4 DCB event schema reference: `DomainEvent` shape, tags taxonomy, metadata envelope, and the `AGGREGATE.ACTION.STATUS` catalog (Story 4-1)
- **Stories.md** - Index of all user stories across all 26 epics (~221 stories, 50+ task plans)
- **Contributing.md** - Contributing guidelines for developers

### Epic Pages (All 25 Epics)

Epic pages live in the `Epics/` subdirectory for better wiki sidebar navigation.

| Epic | Page | Stories | Status |
|------|------|---------|--------|
| 1 | [Epics/Epic-1-Foundation.md](Epics/Epic-1-Foundation) | 15 | Done |
| 1.5 | [Epics/Epic-1.5-Infrastructure.md](Epics/Epic-1.5-Infrastructure) | 15 | Done |
| 2 | [Epics/Epic-2-Autonomous-Loop.md](Epics/Epic-2-Autonomous-Loop) | 16 | Planned |
| 3 | [Epics/Epic-3-Quality-Gates.md](Epics/Epic-3-Quality-Gates) | 12 | Planned |
| 4 | [Epics/Epic-4-Event-Sourcing.md](Epics/Epic-4-Event-Sourcing) | 8 | Planned |
| 5 | [Epics/Epic-5-Observability.md](Epics/Epic-5-Observability) | 15 | Partial |
| 6 | [Epics/Epic-6-Context-Knowledge.md](Epics/Epic-6-Context-Knowledge) | 10 | Done |
| 7 | [Epics/Epic-7-Mentorship.md](Epics/Epic-7-Mentorship) | 19 | Done |
| 8 | [Epics/Epic-8-Distribution.md](Epics/Epic-8-Distribution) | 8 | Planned |
| 9 | [Epics/Epic-9-Agent-Management.md](Epics/Epic-9-Agent-Management) | 11 | Done |
| 10 | [Epics/Epic-10-Engine-Core.md](Epics/Epic-10-Engine-Core) | 8 | Done |
| 11 | [Epics/Epic-11-Security.md](Epics/Epic-11-Security) | 5 | Done |
| 12 | [Epics/Epic-12-Tool-Loop.md](Epics/Epic-12-Tool-Loop) | 4 | Done |
| 13 | [Epics/Epic-13-Workflow-Decomposition.md](Epics/Epic-13-Workflow-Decomposition) | 3 | Done |
| 14 | [Epics/Epic-14-ELSA-Studio.md](Epics/Epic-14-ELSA-Studio) | 3 | Done |
| 15 | [Epics/Epic-15-Log-Aggregation.md](Epics/Epic-15-Log-Aggregation) | 1 (done) + 2 (planned) | Done |
| 16 | [Epics/Epic-16-Auth-Admin.md](Epics/Epic-16-Auth-Admin) | 6 | Done |
| 17 | [Epics/Epic-17-Multi-Tenancy.md](Epics/Epic-17-Multi-Tenancy) | 5 | Planned |
| 18 | [Epics/Epic-18-User-Auth.md](Epics/Epic-18-User-Auth) | 5 | Planned |
| 19 | [Epics/Epic-19-Agent-Dispatch.md](Epics/Epic-19-Agent-Dispatch) | 5 | Planned |
| 20 | [Epics/Epic-20-Billing.md](Epics/Epic-20-Billing) | 5 | Planned |
| 21 | [Epics/Epic-21-Marketing-Dashboard.md](Epics/Epic-21-Marketing-Dashboard) | 5 | Partial |
| 22 | [Epics/Epic-22-CLI-Standalone.md](Epics/Epic-22-CLI-Standalone) | 4 | Planned |
| 23 | [Epics/Epic-23-System-Monitoring.md](Epics/Epic-23-System-Monitoring) | 12 | Planned |
| 24 | [Epics/Epic-24-Voice-Conversation.md](Epics/Epic-24-Voice-Conversation) | 7 | Planned |
| 25 | [Epics/Epic-25-Wiki-Site.md](Epics/Epic-25-Wiki-Site) | 1 | Planned |

**Combined page**: [Epics/Epic-11-14-ELSA.md](Epics/Epic-11-14-ELSA) covers Epics 11-14 together (15 stories total)

## How to Update the GitHub Wiki

### Initial Setup (First Time)

1. Go to https://github.com/meywd/tamma/wiki
2. Click "Create the first page" to initialize the wiki
3. Copy content from `Home.md` and save
4. Clone the wiki repository:
   ```bash
   git clone https://github.com/meywd/tamma.wiki.git
   cd tamma.wiki
   ```
5. Copy all files from this directory to the wiki repo (preserving subdirectories):
   ```bash
   cp -r /path/to/tamma/wiki/* .
   ```
6. Commit and push:
   ```bash
   git add .
   git commit -m "Initialize Tamma wiki with comprehensive documentation"
   git push
   ```

### Updating Pages

1. Edit the markdown files in this `wiki/` directory
2. Commit changes to the main repository
3. Copy updated files to the wiki repository and push:
   ```bash
   cd tamma.wiki
   cp -r /path/to/tamma/wiki/* .
   git add -A
   git commit -m "Update wiki pages"
   git push
   ```

### Auto-Sync (Optional)

Add a GitHub Action to auto-sync wiki changes:

```yaml
name: Sync Wiki
on:
  push:
    paths:
      - 'wiki/**'
    branches:
      - main

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Sync to Wiki
        run: |
          git clone https://github.com/meywd/tamma.wiki.git wiki-repo
          cp -r wiki/* wiki-repo/
          cd wiki-repo
          git config user.name "GitHub Actions"
          git config user.email "actions@github.com"
          git add -A
          git commit -m "Auto-sync from main repo" || exit 0
          git push
```

## Notes

- Wiki pages use GitHub Flavored Markdown
- Internal wiki links use the format `[Link Text](Page-Name)` or `[Link Text](Folder/Page-Name)`
- Epic pages are in the `Epics/` subdirectory for folder-based sidebar navigation
- External links use full URLs
- All story documents are in `/docs/stories/` in the main repository
