# Tamma Wiki Pages

This directory contains the source markdown files for the Tamma GitHub Wiki.

## Pages

- **Home.md** - Wiki homepage with quick links and project overview
- **Roadmap.md** - Project roadmap with epic breakdown and timeline
- **Epic-1-Foundation.md** - Detailed Epic 1 breakdown with all 86 tasks
- **Architecture.md** - System architecture overview
- **Contributing.md** - Contributing guidelines for developers
- **Stories.md** - Index of all user stories across epics

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
