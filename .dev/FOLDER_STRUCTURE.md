# .dev Folder Structure - Quick Reference

## 🎯 Purpose

Organized knowledge base for development artifacts, ensuring no knowledge is lost.

## 📁 Structure

```
.dev/
├── README.md                      # Complete guide to this directory
├── FOLDER_STRUCTURE.md           # This quick reference
│
├── spikes/                        # 🔬 Research & Prototyping
│   ├── YYYY-MM-DD-spike-name.md
│   └── [Research findings before implementation]
│
├── bugs/                          # 🐛 Bug Reports & Resolutions
│   ├── YYYY-MM-DD-bug-name.md
│   └── [Root cause analysis and fixes]
│
├── findings/                      # 💡 Knowledge & Learnings
│   ├── YYYY-MM-DD-finding-name.md
│   ├── [Pitfalls - ⚠️]
│   ├── [Known Issues - 🚨]
│   ├── [Lessons Learned - 📚]
│   ├── [Best Practices - 🔍]
│   └── [Performance Notes - ⚡]
│
├── decisions/                     # 🏗️ Architecture Decision Records
│   ├── YYYY-MM-DD-decision-name.md
│   └── [Why we chose X over Y]
│
└── templates/                     # 📝 Document Templates
    ├── spike-template.md
    ├── bug-template.md
    ├── finding-template.md
    └── decision-template.md
```

## 🚀 Quick Start

### Before You Code
```bash
# 1. ALWAYS read this first
cat BEFORE_YOU_CODE.md

# 2. Search existing knowledge
grep -r "your-topic" .dev/

# 3. Check specific areas
ls .dev/spikes/ | grep -i "your-area"
ls .dev/bugs/ | grep -i "your-feature"
ls .dev/findings/ | grep -i "your-concern"
```

### Creating Documents
```bash
# Copy template
cp .dev/templates/spike-template.md .dev/spikes/$(date +%Y-%m-%d)-my-spike.md

# Edit
vim .dev/spikes/$(date +%Y-%m-%d)-my-spike.md
```

## 📋 Document Types

### 🔬 Spike (spikes/)
**When**: Researching unfamiliar tech or choosing between approaches
**Contains**: Research findings, comparisons, recommendations
**Example**: `2025-10-29-ai-provider-comparison.md`

### 🐛 Bug (bugs/)
**When**: Discovered a bug
**Contains**: Reproduction steps, root cause, fix, tests
**Example**: `2025-10-29-postgres-connection-leak.md`

### 💡 Finding (findings/)
**When**: Learned something important
**Types**:
- ⚠️ Pitfall - Things that break
- 🚨 Known Issue - Limitations we accept
- 📚 Lesson Learned - What worked/didn't
- 🔍 Best Practice - Patterns to follow
- ⚡ Performance Note - Optimization insights
**Example**: `2025-10-29-pitfall-anthropic-rate-limits.md`

### 🏗️ Decision (decisions/)
**When**: Making architectural choice
**Contains**: Context, options, decision, rationale
**Example**: `2025-10-29-decision-use-pino-for-logging.md`

## 🎯 Naming Convention

**Format**: `YYYY-MM-DD-descriptive-name.md`

**Good Names**:
- ✅ `2025-10-29-compare-event-sourcing-patterns.md`
- ✅ `2025-10-30-fix-memory-leak-in-worker-pool.md`
- ✅ `2025-10-31-pitfall-fastify-plugin-load-order.md`

**Bad Names**:
- ❌ `spike1.md` (not descriptive)
- ❌ `bug.md` (not specific)
- ❌ `notes.md` (too generic)

## 🔍 Search Cheatsheet

```bash
# Find all research on a topic
grep -r "anthropic" .dev/spikes/

# Find critical bugs
grep -r "🔴 Critical" .dev/bugs/

# Find performance findings
grep -r "⚡ Performance" .dev/findings/

# Find recent decisions (last 30 days)
find .dev/decisions -name "*.md" -mtime -30

# Find open bugs
grep -r "🐛 Open" .dev/bugs/

# Search everything
grep -r "postgres" .dev/
```

## ✅ Status Indicators

### Spikes
- 🔍 Research - Ongoing
- ✅ Complete - Done
- 🚫 Abandoned - Discontinued

### Bugs
- 🐛 Open - New
- 🔍 Investigating - Analyzing
- 🔧 In Progress - Fixing
- ✅ Resolved - Fixed
- 🚫 Won't Fix - Accepted

### Findings
- ✅ Validated - Confirmed
- 🔍 Needs Review - Pending
- 📝 Draft - WIP

### Decisions
- 🤔 Proposed - Considering
- ✅ Accepted - Chosen
- 🚫 Rejected - Not chosen
- ⏸️ Superseded - Replaced

## 🔗 Cross-Reference Template

```markdown
## Related

- Related spike: `.dev/spikes/YYYY-MM-DD-spike.md`
- Related bug: `.dev/bugs/YYYY-MM-DD-bug.md`
- Related finding: `.dev/findings/YYYY-MM-DD-finding.md`
- Related decision: `.dev/decisions/YYYY-MM-DD-decision.md`
- GitHub Issue: #123
- Pull Request: #456
- Story: `docs/stories/1-1-story-name.md`
```

## 🎓 For AI Agents

### MUST DO Before Coding
1. ✅ Read `BEFORE_YOU_CODE.md`
2. ✅ Search `.dev/` for existing work
3. ✅ Check relevant story in `docs/stories/`
4. ✅ Review related decisions
5. ✅ Note any pitfalls/known issues

### During Development
- Document as you go
- Create spike if researching
- Create finding if learned something
- Create bug if found issue
- Create decision if choosing approach

### After Implementation
- Update spike status
- Mark bug as resolved
- Document lessons learned
- Link to implementation

## 📊 Workflow Examples

### Example 1: Implementing New Feature

```bash
# 1. Check existing knowledge
grep -r "feature-name" .dev/

# 2. Create spike if needed
cp .dev/templates/spike-template.md .dev/spikes/2025-10-29-feature-research.md
# ... do research ...

# 3. Create decision
cp .dev/templates/decision-template.md .dev/decisions/2025-10-29-chose-approach-x.md

# 4. Implement
# ... write code ...

# 5. Document learnings
cp .dev/templates/finding-template.md .dev/findings/2025-10-29-learned-about-x.md
```

### Example 2: Fixing Bug

```bash
# 1. Create bug report
cp .dev/templates/bug-template.md .dev/bugs/2025-10-29-app-crashes.md

# 2. Investigate and document
# ... add findings to bug report ...

# 3. Implement fix
# ... write fix ...

# 4. Update bug status to resolved
# ... mark as ✅ Resolved ...

# 5. Create finding if learned something
cp .dev/templates/finding-template.md .dev/findings/2025-10-29-pitfall-discovered.md
```

## 💡 Pro Tips

### For Efficiency
- Use templates consistently
- Name files descriptively
- Link related documents
- Update status promptly
- Include code samples

### For Quality
- Write for future you
- Be specific and detailed
- Include examples
- Document context
- Explain rationale

### For Team
- Share learnings widely
- Review findings weekly
- Archive resolved bugs
- Extract patterns monthly

## 🚫 Common Mistakes

❌ **DON'T**:
- Create orphaned docs (no links)
- Use generic names
- Skip templates
- Forget to update status
- Write vague descriptions

✅ **DO**:
- Link related documents
- Use descriptive names
- Follow templates
- Keep status current
- Write clear details

## 📚 Further Reading

- `BEFORE_YOU_CODE.md` - Mandatory process
- `.dev/README.md` - Complete guide
- `CLAUDE.md` - Project guidelines
- `docs/architecture.md` - Technical architecture

---

**Quick Reference Version**: v1.0
**Last Updated**: October 29, 2025
