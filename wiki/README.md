# Tamma Wiki Pages

This directory contains the source markdown files for the Tamma GitHub Wiki.

## Pages

- **Home.md** - Wiki homepage with quick links, project overview, and status
- **Roadmap.md** - Project roadmap with all 24 epics and timeline
- **Architecture.md** - System architecture (dual TypeScript + C#/ELSA stack)
- **Epic-1-Foundation.md** - Epic 1: Foundation & Core Infrastructure (15 stories)
- **Epic-1.5-Infrastructure.md** - Epic 1.5: Infrastructure & Deployment (15 stories)
- **Epic-6-Context-Knowledge.md** - Epic 6: Context & Knowledge Management (10 stories)
- **Epic-7-Mentorship.md** - Epic 7: Autonomous Mentorship Workflow (19 stories)
- **Epic-9-Agent-Management.md** - Epic 9: Config-driven multi-agent system (11 stories)
- **Epic-10-Engine-Core.md** - Epic 10: Engine Core -- Workflow-Driven Architecture (8 stories)
- **Epic-11-14-ELSA.md** - Epics 11-14: Security Hardening, Agentic Tool Loop, Workflow Decomposition, Custom Studio (15 stories)
- **Epic-23-System-Monitoring.md** - Epic 23: System Monitoring & Observability Dashboard (12 stories)
- **Epic-24-Voice-Conversation.md** - Epic 24: Realtime Voice Conversation (7 stories)
- **Stories.md** - Index of all user stories across all 24 epics (~220 stories)
- **Contributing.md** - Contributing guidelines for developers

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
