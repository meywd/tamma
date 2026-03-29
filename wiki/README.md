# Tamma Wiki Pages

This directory contains the source markdown files for the Tamma GitHub Wiki.

## Pages

### General

- **Home.md** - Wiki homepage with quick links, project overview, and status
- **Roadmap.md** - Project roadmap with all 24 epics and timeline
- **Architecture.md** - System architecture (dual TypeScript + C#/ELSA stack)
- **Stories.md** - Index of all user stories across all 24 epics (~220 stories, 50+ task plans)
- **Contributing.md** - Contributing guidelines for developers

### Epic Pages (All 24 Epics)

| Epic | Page | Stories | Status |
|------|------|---------|--------|
| 1 | [Epic-1-Foundation.md](Epic-1-Foundation) | 15 | Done |
| 1.5 | [Epic-1.5-Infrastructure.md](Epic-1.5-Infrastructure) | 15 | Done |
| 2 | [Epic-2-Autonomous-Loop.md](Epic-2-Autonomous-Loop) | 16 | Planned |
| 3 | [Epic-3-Quality-Gates.md](Epic-3-Quality-Gates) | 12 | Planned |
| 4 | [Epic-4-Event-Sourcing.md](Epic-4-Event-Sourcing) | 8 | Planned |
| 5 | [Epic-5-Observability.md](Epic-5-Observability) | 15 | Partial |
| 6 | [Epic-6-Context-Knowledge.md](Epic-6-Context-Knowledge) | 10 | Done |
| 7 | [Epic-7-Mentorship.md](Epic-7-Mentorship) | 19 | Done |
| 8 | [Epic-8-Distribution.md](Epic-8-Distribution) | 8 | Planned |
| 9 | [Epic-9-Agent-Management.md](Epic-9-Agent-Management) | 11 | Done |
| 10 | [Epic-10-Engine-Core.md](Epic-10-Engine-Core) | 8 | Done |
| 11 | [Epic-11-Security.md](Epic-11-Security) | 5 | Done |
| 12 | [Epic-12-Tool-Loop.md](Epic-12-Tool-Loop) | 4 | Done |
| 13 | [Epic-13-Workflow-Decomposition.md](Epic-13-Workflow-Decomposition) | 3 | Done |
| 14 | [Epic-14-ELSA-Studio.md](Epic-14-ELSA-Studio) | 3 | Done |
| 15 | [Epic-15-Log-Aggregation.md](Epic-15-Log-Aggregation) | 1 (done) + 2 (planned) | Done |
| 16 | [Epic-16-Auth-Admin.md](Epic-16-Auth-Admin) | 6 | Done |
| 17 | [Epic-17-Multi-Tenancy.md](Epic-17-Multi-Tenancy) | 5 | Planned |
| 18 | [Epic-18-User-Auth.md](Epic-18-User-Auth) | 5 | Planned |
| 19 | [Epic-19-Agent-Dispatch.md](Epic-19-Agent-Dispatch) | 5 | Planned |
| 20 | [Epic-20-Billing.md](Epic-20-Billing) | 5 | Planned |
| 21 | [Epic-21-Marketing-Dashboard.md](Epic-21-Marketing-Dashboard) | 5 | Partial |
| 22 | [Epic-22-CLI-Standalone.md](Epic-22-CLI-Standalone) | 4 | Planned |
| 23 | [Epic-23-System-Monitoring.md](Epic-23-System-Monitoring) | 12 | Planned |
| 24 | [Epic-24-Voice-Conversation.md](Epic-24-Voice-Conversation) | 7 | Planned |

**Combined page**: [Epic-11-14-ELSA.md](Epic-11-14-ELSA) covers Epics 11-14 together (15 stories total)

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
5. Copy all `.md` files from this directory to the wiki repo:
   ```bash
   cp /path/to/tamma/wiki/*.md .
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
   cp /path/to/tamma/wiki/[changed-file].md .
   git add [changed-file].md
   git commit -m "Update [page-name]"
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
          cp wiki/*.md wiki-repo/
          cd wiki-repo
          git config user.name "GitHub Actions"
          git config user.email "actions@github.com"
          git add .
          git commit -m "Auto-sync from main repo" || exit 0
          git push
```

## Notes

- Wiki pages use GitHub Flavored Markdown
- Internal wiki links use the format `[Link Text](Page-Name)`
- External links use full URLs
- All story documents are in `/docs/stories/` in the main repository
