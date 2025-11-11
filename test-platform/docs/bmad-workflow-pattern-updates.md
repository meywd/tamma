# Test-Platform BMad Workflow Pattern Updates

## Overview

Updated test-platform BMad story workflows to follow the new `epic-X/story-Y-Z/` organizational pattern established for the Tamma project, ensuring consistency between main project and test-platform.

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
- Follows the pattern established in main project

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

## Consistency with Main Project

### Alignment Achieved

✅ **Same folder structure:** `epic-X/story-Y-Z/` pattern  
✅ **Same naming conventions:** Y-Z- prefixed files  
✅ **Same task breakdown pattern:** task-N.md files  
✅ **Same workflow integration:** All workflows support new pattern  
✅ **Same template structure:** Consistent task breakdown format

### Benefits

1. **Consistency:** Both repositories follow identical patterns
2. **Maintainability:** Single pattern to learn and maintain
3. **Portability:** Stories can be moved between repositories
4. **Scalability:** Same approach works for all story types
5. **Workflow compatibility:** BMad workflows work consistently

## Validation

### Pattern Compliance

✅ Follows epic-X/story-Y-Z/ folder structure  
✅ Uses Y-Z- prefixed file naming  
✅ Includes task breakdown files  
✅ Matches main project structure  
✅ BMad workflows updated accordingly

### Test-Platform Specific

✅ Maintains existing test-platform workflows  
✅ Preserves test-platform configuration  
✅ Compatible with test-platform story structure  
✅ Supports test-platform development process

## Benefits

1. **Unified Development:** Consistent patterns across both repositories
2. **Reduced Learning Curve:** Single pattern for all stories
3. **Better Organization:** All story files grouped together
4. **Workflow Compatibility:** BMad workflows work everywhere
5. **Future-Proof:** Scalable for new story types

## Notes

- Test-platform BMad workflows now match main project exactly
- Existing test-platform stories will benefit from new pattern
- Task breakdown files provide detailed implementation guidance
- Pattern supports both simple and complex stories
- Maintains backward compatibility with existing processes

## Next Steps

1. Test updated workflows with test-platform stories
2. Create task breakdown for existing test-platform stories
3. Validate pattern consistency across both repositories
4. Update training documentation for new pattern
