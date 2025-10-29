# .dev - Development Knowledge Base

This directory contains all development documentation, research, and knowledge accumulated during the Tamma project.

## 📁 Directory Structure

```
.dev/
├── README.md                    # This file
├── spikes/                      # Research and prototyping
├── bugs/                        # Bug reports and resolutions
├── findings/                    # Discoveries and learnings
├── decisions/                   # Architecture Decision Records (ADRs)
└── templates/                   # Document templates
    ├── spike-template.md
    ├── bug-template.md
    ├── finding-template.md
    └── decision-template.md
```

## 🎯 Purpose

The `.dev` directory serves as a **knowledge base** for the development team (humans and AI agents). It captures:

1. **Research** - Spikes exploring different approaches
2. **Problems** - Bugs and how they were resolved
3. **Learnings** - Pitfalls, best practices, and lessons learned
4. **Decisions** - Architectural choices and their rationale

## 📚 Folder Descriptions

### 📊 spikes/

**Purpose**: Research and prototyping work

**When to use**: Before implementing something unfamiliar or when choosing between multiple approaches

**Naming Convention**: `YYYY-MM-DD-spike-name.md`

**Examples**:
- `2025-10-29-ai-provider-comparison.md`
- `2025-10-30-event-store-schema-design.md`
- `2025-10-31-rate-limiting-strategies.md`

**Template**: `.dev/templates/spike-template.md`

### 🐛 bugs/

**Purpose**: Bug reports with root cause analysis and resolution

**When to use**: When a bug is discovered during development or testing

**Naming Convention**: `YYYY-MM-DD-bug-name.md`

**Examples**:
- `2025-10-29-postgres-connection-leak.md`
- `2025-10-30-cli-crash-on-ctrl-c.md`
- `2025-10-31-anthropic-api-rate-limit-error.md`

**Template**: `.dev/templates/bug-template.md`

### 💡 findings/

**Purpose**: Document discoveries, pitfalls, best practices, and lessons learned

**When to use**: When you learn something important that others should know

**Types of findings**:
- ⚠️ **Pitfalls** - Things that don't work or cause issues
- 🚨 **Known Issues** - Bugs or limitations we're aware of
- 📚 **Lessons Learned** - What worked well or poorly
- 🔍 **Best Practices** - Patterns that should be followed
- ⚡ **Performance Notes** - Optimization discoveries

**Naming Convention**: `YYYY-MM-DD-finding-name.md`

**Examples**:
- `2025-10-29-pitfall-anthropic-rate-limits.md`
- `2025-10-30-best-practice-fastify-plugin-pattern.md`
- `2025-10-31-performance-pino-vs-winston.md`

**Template**: `.dev/templates/finding-template.md`

### 🏗️ decisions/

**Purpose**: Architecture Decision Records (ADRs)

**When to use**: When making significant architectural or technical decisions

**Naming Convention**: `YYYY-MM-DD-decision-name.md`

**Examples**:
- `2025-10-29-decision-use-pino-for-logging.md`
- `2025-10-30-decision-vitest-over-jest.md`
- `2025-10-31-decision-dcb-event-sourcing-pattern.md`

**Template**: `.dev/templates/decision-template.md`

## 🔍 How to Use This Directory

### Before You Code

**ALWAYS check these folders first:**

```bash
# Search for existing research
grep -r "your-topic" .dev/spikes/

# Check for known bugs
grep -r "your-feature" .dev/bugs/

# Look for relevant findings
grep -r "your-area" .dev/findings/

# Review related decisions
ls .dev/decisions/ | grep -i "your-area"
```

### During Development

**Document as you go:**

1. **Found a bug?** → Create a bug report
2. **Researching something?** → Create a spike
3. **Learned something important?** → Create a finding
4. **Making an architectural choice?** → Create a decision

### After Implementation

**Update documentation:**

1. Mark spikes as complete
2. Update bug status to resolved
3. Create findings for lessons learned
4. Link decisions to implemented code

## 📝 Creating New Documents

### Quick Start

```bash
# Copy template
cp .dev/templates/spike-template.md .dev/spikes/2025-10-29-my-spike.md

# Edit with your favorite editor
vim .dev/spikes/2025-10-29-my-spike.md

# Follow the template structure
```

### Document Naming

**Format**: `YYYY-MM-DD-descriptive-name.md`

**Examples**:
- ✅ `2025-10-29-compare-ai-providers.md`
- ✅ `2025-10-30-fix-memory-leak.md`
- ❌ `spike.md` (too generic)
- ❌ `bug-123.md` (use descriptive name)

### Document Status

Use status indicators consistently:

**Spikes**:
- 🔍 Research - Active research
- ✅ Complete - Research finished
- 🚫 Abandoned - Research discontinued

**Bugs**:
- 🐛 Open - Bug discovered
- 🔍 Investigating - Root cause analysis
- 🔧 In Progress - Fix being implemented
- ✅ Resolved - Bug fixed
- 🚫 Won't Fix - Decided not to fix

**Findings**:
- ✅ Validated - Confirmed and tested
- 🔍 Needs Review - Requires validation
- 📝 Draft - Work in progress

**Decisions**:
- 🤔 Proposed - Under consideration
- ✅ Accepted - Decision made
- 🚫 Rejected - Not chosen
- ⏸️ Superseded - Replaced by newer decision

## 🔗 Cross-Referencing

**Always link related documents:**

```markdown
## Related

- Related spike: `.dev/spikes/2025-10-29-spike.md`
- Related bug: `.dev/bugs/2025-10-30-bug.md`
- Related finding: `.dev/findings/2025-10-31-finding.md`
- Related decision: `.dev/decisions/2025-10-29-decision.md`
- GitHub Issue: #123
- Story: `docs/stories/1-1-story-name.md`
```

## 📊 Benefits

### For the Team

- 🧠 **Shared Knowledge** - Everyone learns from everyone
- 🎯 **Better Decisions** - Learn from past research
- 🐛 **Faster Debugging** - Known issues are documented
- 📈 **Continuous Improvement** - Lessons are captured

### For AI Agents

- 🤖 **Context Awareness** - Understand past decisions
- 🔍 **Better Research** - Build on existing spikes
- ⚠️ **Avoid Pitfalls** - Learn from documented mistakes
- 📚 **Pattern Learning** - Follow established best practices

### For New Contributors

- 🎓 **Fast Onboarding** - Learn project patterns
- 💡 **Understand Context** - See why decisions were made
- 🛡️ **Avoid Known Issues** - Don't repeat mistakes
- 🚀 **Productive Faster** - Less time asking questions

## 🎯 Best Practices

### DO ✅

- Document immediately (while fresh in memory)
- Use templates consistently
- Link related documents
- Update status as things progress
- Include code samples
- Be specific and detailed
- Think about future readers

### DON'T ❌

- Wait until later to document
- Use generic titles
- Create orphaned documents (no links)
- Leave status outdated
- Write vague descriptions
- Assume everyone knows the context
- Forget to update after resolution

## 🔍 Search Tips

```bash
# Find all spikes about a topic
grep -r "anthropic" .dev/spikes/

# Find high-severity bugs
grep -r "🔴 Critical" .dev/bugs/

# Find performance-related findings
grep -r "Performance" .dev/findings/

# Find recent decisions (last 30 days)
find .dev/decisions -name "*.md" -mtime -30

# Find all open bugs
grep -r "🐛 Open" .dev/bugs/

# Search across all .dev files
grep -r "postgres" .dev/
```

## 📅 Maintenance

### Weekly

- [ ] Update bug statuses
- [ ] Mark completed spikes
- [ ] Review and validate findings

### Monthly

- [ ] Review outdated documents
- [ ] Archive resolved bugs
- [ ] Create summary of learnings

### Quarterly

- [ ] Extract patterns for documentation
- [ ] Update team knowledge base
- [ ] Create training materials

## 🤝 Contributing

### Adding Templates

1. Create template in `.dev/templates/`
2. Use markdown format
3. Include comprehensive sections
4. Add examples
5. Update this README

### Improving Process

1. Document suggestion in `.dev/findings/`
2. Open GitHub Discussion
3. Get team feedback
4. Update process
5. Update `BEFORE_YOU_CODE.md`

## 📚 External Resources

- [Architecture Decision Records](https://adr.github.io/)
- [Writing Technical Documentation](https://developers.google.com/tech-writing)
- [Spike User Stories](https://www.jpattonassociates.com/spikes/)

## 💬 Questions?

If you're unsure about:
- **What to document** → See `BEFORE_YOU_CODE.md`
- **How to document** → Use templates in `.dev/templates/`
- **When to document** → Document immediately!
- **Why document** → Re-read this README

---

**Remember**: Future you (and your teammates) will thank you for documenting well!

**Last Updated**: October 29, 2025
