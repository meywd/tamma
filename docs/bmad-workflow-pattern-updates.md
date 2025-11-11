# BMad Workflow Pattern Updates

## Overview

Updated BMad story workflows to follow the new `epic-X/story-Y-Z/` organizational pattern established for the Tamma project.

## Changes Made

### 1. create-story Workflow

**File:** `bmad/bmm/workflows/4-implementation/create-story/workflow.yaml`

**Updates:**

- Added `story_folder` variable: `{story_dir}/epic-{{epic_num}}/story-{{epic_num}}-{{story_num}}`
- Updated `default_output_file` to use new folder structure
- Added `context_output_file` for context.xml placement
- Added `story_key` variable for consistent naming

### 2. Story Template

**File:** `bmad/bmm/workflows/4-implementation/create-story/template.md`

**Updates:**

- Added "Task Breakdown Files" section with documentation
- Documents the new task file naming pattern: `{{story_key}}-task-N.md`

### 3. Task Template

**File:** `bmad/bmm/workflows/4-implementation/create-story/task-template.md` (NEW)

**Purpose:**

- Standardized template for individual task breakdown files
- Includes acceptance criteria, implementation details, testing strategy
- Follows the pattern observed in test-platform stories

### 4. dev-story Workflow

**File:** `bmad/bmm/workflows/4-implementation/dev-story/workflow.yaml`

**Updates:**

- Added `story_folder` variable derived from story file path
- Updated `context_file` path to use story folder
- Added `task_files_pattern` for discovering task breakdown files

### 5. story-context Workflow

**File:** `bmad/bmm/workflows/4-implementation/story-context/workflow.yaml`

**Updates:**

- Added `story_folder` variable following new pattern
- Updated `default_output_file` to place context.xml in story folder

### 6. story-ready & story-done Workflows

**Files:**

- `bmad/bmm/workflows/4-implementation/story-ready/workflow.yaml`
- `bmad/bmm/workflows/4-implementation/story-done/workflow.yaml`

**Updates:**

- Added comments documenting new story path pattern
- No functional changes needed (status-only workflows)

## New Pattern Implementation

### File Structure

```
docs/stories/
├── epic-X/
│   ├── story-Y-Z/
│   │   ├── Y-Z-story-name.md              # Main story file
│   │   ├── Y-Z-story-name.context.xml      # Story context
│   │   ├── Y-Z-story-name-task-1.md        # Task 1 breakdown
│   │   ├── Y-Z-story-name-task-2.md        # Task 2 breakdown
│   │   └── Y-Z-story-name-task-N.md        # Task N breakdown
│   └── tech-spec-epic-X.md                 # Epic technical spec
```

### Naming Conventions

- **Story folders:** `epic-X/story-Y-Z/`
- **Main story:** `Y-Z-story-name.md`
- **Context file:** `Y-Z-story-name.context.xml`
- **Task files:** `Y-Z-story-name-task-N.md`
- **Tech specs:** `tech-spec-epic-X.md`

### Workflow Integration

- **create-story:** Generates folder structure and main files
- **story-context:** Creates context.xml in story folder
- **dev-story:** Reads from story folder and task files
- **story-ready/story-done:** Update status using story path

## Validation

### Test Story Created

**Location:** `docs/stories/epic-1/story-1-99/`
**Files:**

- `1-99-test-story-pattern.md` - Main story
- `1-99-test-story-pattern.context.xml` - Context
- `1-99-test-story-pattern-task-1.md` - Task breakdown

### Pattern Compliance

✅ Follows epic-X/story-Y-Z/ folder structure  
✅ Uses Y-Z- prefixed file naming  
✅ Includes task breakdown files  
✅ Matches test-platform structure  
✅ BMad workflows updated accordingly

## Benefits

1. **Consistency:** Aligns with test-platform structure
2. **Organization:** All story files grouped together
3. **Scalability:** Easy to add new tasks and stories
4. **Maintainability:** Clear naming and structure patterns
5. **Workflow Integration:** BMad workflows support new pattern

## Next Steps

1. Test BMad workflows with real story creation
2. Update any remaining workflow references
3. Train team on new organizational pattern
4. Consider migration strategy for existing stories

## Notes

- BMad directory is gitignored, so workflow changes are local
- Test story committed as validation of pattern
- Existing stories will need migration to new pattern
- Task breakdown files are optional but recommended for complex stories
