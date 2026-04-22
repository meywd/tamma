# Story 27-4: Prompt Store Admin UI

Status: ready-for-dev

## Story

As a **platform administrator**,
I want an admin panel page for managing system default prompts with rich contextual information — wiki documentation for each prompt type, LLM capability data for provider-specific tuning, and a complete variable/keyword reference,
so that I can make informed prompt engineering decisions without guessing what each prompt does, which LLM strengths to exploit, or what variables are available.

## Acceptance Criteria

### Core UI (Prompt Management)

1. A "Prompts" page is accessible from the admin panel navigation under "System > Prompts"
2. The page displays a table of all system default prompts (80 role+action templates) with columns: Role, Action, Version, Enable Tools, Max Tokens, Last Updated
3. The table supports filtering by role (dropdown) and action (dropdown)
4. The table supports text search across template content
5. Clicking a row opens an edit dialog/drawer with: template editor (monospaced, syntax-highlighted textarea), variable list (auto-extracted from `{{...}}` patterns), system prompt textarea, tools toggle, max tokens input
6. The edit dialog has a "Save" button that calls `PUT /api/prompts/system/:role/:action`
7. The edit dialog has a "Reset to Default" button that calls `DELETE /api/prompts/system/:role/:action` to restore the hardcoded default
8. A separate "System Prompts" tab shows the 8 role system prompts with inline editing
9. A separate "Action Defaults" tab shows the 10 action default templates with editing
10. All changes require confirmation dialog
11. Error states are displayed inline (API failures, validation errors)
12. Only platform admin users (owner role) can access this page

### Wiki Integration (Prompt Documentation)

13. Each role (8) and action (10) has a wiki page at `wiki/Prompts/Role-{role}.md` and `wiki/Prompts/Action-{action}.md` explaining:
    - What the role/action does in the Tamma workflow
    - When it's triggered (which Elsa workflow/activity invokes it)
    - What the expected output format is
    - Tips for customization
    - Common pitfalls
14. Each role+action combination (80) has a short description sourced from a wiki index page `wiki/Prompts/Index.md` with a table mapping (role, action) → one-line description
15. The edit dialog shows a "Documentation" panel alongside the template editor, pulled from the wiki. This panel shows:
    - Role description (from `wiki/Prompts/Role-{role}.md`)
    - Action description (from `wiki/Prompts/Action-{action}.md`)
    - The one-line description for this specific role+action combination
16. Documentation is fetched from the wiki site API (`/content/prompts/role-{role}.md`) and cached client-side for the session
17. If no wiki page exists for a role/action, show a "Documentation not yet available" placeholder with a link to create it

### LLM Context Panel

18. The edit dialog shows an "LLM Info" panel that displays the currently selected LLM provider for this role (from agent config), including:
    - Provider name and model (e.g., "Anthropic Claude Sonnet 4")
    - Context window size
    - Max output tokens
    - Key strengths (e.g., "strong at code generation, reasoning, instruction following")
    - Key weaknesses (e.g., "may over-explain, verbose on simple tasks")
    - Benchmark scores summary (coding, reasoning, math, instruction following)
    - Last updated date of the LLM data
19. LLM capability data is stored in a static JSON file (`packages/shared/src/data/llm-profiles.json`) containing profiles for all supported providers, sourced from public benchmarks and data cards
20. The "LLM Info" panel highlights which strengths are relevant to the current action (e.g., for "implement" action, highlight "code generation" strength)
21. When a prompt is provider-specific (has a `provider` dimension), the panel shows a provider selector dropdown to view prompts tuned for different LLMs

### Variable & Keyword Reference

22. The edit dialog shows a "Variables" reference panel listing ALL available template variables with:
    - Variable name (e.g., `{{conventions}}`, `{{issue_body}}`, `{{file_list}}`)
    - Type (string, array, object)
    - Source (where the value comes from — e.g., "repo config", "GitHub issue", "context scan output")
    - Description of what it contains
    - Example value (truncated)
    - Which actions typically use it
23. **Inline syntax coloring**: The template editor renders `{{variable}}` tokens in a distinct color (purple/violet) inline as the user types. This is NOT a plain textarea — use a code editor component (CodeMirror 6 or a lightweight contenteditable overlay) that supports custom token highlighting. Specifically:
    - `{{variable_name}}` tokens: highlighted in purple with a subtle background tint
    - Markdown headers (`## Section`): bold
    - Markdown bullets/lists: dimmed prefix
    - Known prompt keywords (from the keyword list): highlighted in blue when they appear in the template text
    - Unknown/invalid variables (e.g., `{{typo_var}}` not in the reference list): highlighted in red/orange as a warning
24. **Autocomplete for variables**: Typing `{{` triggers an autocomplete dropdown showing all available variables with their descriptions. The dropdown filters as the user types (e.g., typing `{{con` shows `{{conventions}}`, `{{context}}`). Selecting from the dropdown inserts the full `{{variable}}` token. The autocomplete popup shows: variable name, type badge, and one-line description.
25. **Autocomplete for keywords**: Typing a recognized keyword prefix (3+ characters) shows a subtle inline suggestion (ghost text or dropdown) for matching prompt engineering keywords relevant to the current action. This is opt-in (can be toggled off in editor settings).
26. Clicking a variable name in the reference panel inserts it at the cursor position in the template editor
27. A "Keywords" section lists prompt engineering keywords/techniques relevant to the current action:
    - For "implement": "step-by-step", "code only", "no explanation", "follow conventions"
    - For "code-review": "severity levels", "false positive rate", "security focus"
    - For "plan": "break down", "dependencies", "risk assessment", "estimation"
28. Keywords appearing in the template text are colored inline (blue) to visually distinguish them from regular prose
29. Hovering over a highlighted variable or keyword in the editor shows a tooltip with: description, source, type, and example value
30. Variable and keyword reference data is stored in `packages/shared/src/data/prompt-variables.json` and `packages/shared/src/data/prompt-keywords.json`

### Prompt Verification (LLM Review)

31. The edit view has a "Verify Prompt" button that sends the current template to the orchestrator for LLM evaluation. The orchestrator:
    - Receives the template text, role, action, variable list, the target LLM profile, AND a **purpose context** bundle containing:
      - Which Elsa workflow/activity invokes this prompt (from wiki docs, e.g., "SingleIssueCycleWorkflow → ImplementActivity")
      - What the expected output format is (e.g., "git diff", "JSON plan object", "markdown review")
      - What the downstream consumer does with the result (e.g., "applied as a git commit", "parsed as JSON and passed to TaskCreationWorkflow", "displayed to the user for approval")
      - The wiki documentation for this role and action (pulled from `wiki/Prompts/`)
    - This purpose context is assembled automatically from `prompt-variables.json` (which has `usedBy` per variable) and the wiki docs (which describe workflow triggers and output expectations). The admin does NOT need to type it manually.
    - Calls the LLM with a meta-prompt: "You are a prompt engineering expert. Review this prompt template for the `{role}/{action}` task targeting `{llm_model}`. The prompt is used by `{workflow}` and its output is consumed as `{output_format}` by `{downstream_consumer}`. Evaluate: clarity, completeness, variable usage, instruction specificity, output format definition, edge case handling, and alignment with the LLM's strengths AND the downstream consumer's expectations. Return structured feedback."
    - Returns a structured review with:
      - **Score** (1-10) for overall prompt quality
      - **Strengths**: what the prompt does well
      - **Issues**: specific problems (missing output format, ambiguous instructions, unused variables, variables referenced but not in the available list, prompt too long for the model's effective context, instructions that conflict with the LLM's known weaknesses)
      - **Suggestions**: concrete rewrites for problematic sections
      - **Variable audit**: lists variables used in template vs. available variables — flags unused available variables that would improve the prompt, and used variables not in the reference list
32. The verification result is displayed in a "Review" panel below the editor with color-coded severity (green/yellow/red) for each issue
33. The "Suggestions" section has "Apply" buttons that patch the specific section of the template with the suggested rewrite (user reviews the diff before accepting)
34. Verification is async — shows a loading spinner and doesn't block editing. Results are cached for the current template text (re-verify only if text changes).
35. The verification endpoint is `POST /api/prompts/verify` which delegates to the orchestrator's LLM call workflow with a dedicated `prompt-review` action
36. The verify request includes the **system default** template alongside the current edit. The LLM review compares the two and reports:
    - What was added vs. the system default
    - What was removed vs. the system default
    - Whether removals break expected behavior (e.g., removing a critical `{{variable}}` that the workflow injects)
    - A "drift score" indicating how far the custom prompt has diverged from the default
37. The edit view shows a "Compare with Default" toggle that displays a side-by-side or inline diff of the current template vs. the system default (without needing the LLM — pure text diff). This is always available, not just during verification.

### Convention Templates

27. A "Convention Templates" section shows the 20 convention templates (read-only) with preview and copy-to-clipboard
28. Selecting a convention template shows how `{{conventions}}` would be populated for that template

### Weekly Wiki Refresh Job

29. A scheduled Elsa workflow (`PromptWikiRefreshWorkflow`) runs weekly (configurable cron) that:
    - Scans all 80 role+action prompts and checks if wiki documentation exists
    - For missing wiki pages, generates draft documentation using the LLM (context: prompt template, role description, action description, variable list)
    - For existing wiki pages, checks if the prompt template has changed since the wiki was last updated and flags stale docs
    - Generates a summary report of wiki coverage (e.g., "72/80 documented, 5 stale, 3 missing")
    - Posts the report to the admin dashboard notifications
30. The refresh job does NOT auto-publish generated docs — it creates drafts that admins review and approve
31. The refresh job updates LLM profile data by fetching latest benchmark data from provider data cards (Anthropic, OpenAI model cards) and updating `llm-profiles.json` — also as a draft for admin review

## Technical Context

### Dashboard Stack

The admin dashboard is a React SPA served from `app.tamma.dev`. It uses:
- React 19 with Vite
- Tailwind CSS for styling
- React Router for navigation
- Fetch API for HTTP calls to `api.tamma.dev`

### Wiki Integration Architecture

The wiki site (`wiki.tamma.dev`) serves markdown content from `apps/wiki-site/public/content/`. The prompt documentation pages will be added under `apps/wiki-site/public/content/prompts/`. The dashboard fetches these via the wiki site's content API or directly from the Git-hosted markdown.

### LLM Profiles Data Structure

```json
{
  "anthropic/claude-sonnet-4": {
    "provider": "anthropic",
    "model": "claude-sonnet-4",
    "displayName": "Claude Sonnet 4",
    "contextWindow": 200000,
    "maxOutputTokens": 64000,
    "strengths": ["code generation", "reasoning", "instruction following", "long context"],
    "weaknesses": ["may over-explain"],
    "benchmarks": {
      "humaneval": 92.1,
      "swe-bench-verified": 72.5,
      "gpqa-diamond": 68.4,
      "mmlu-pro": 84.2
    },
    "actionAffinity": {
      "implement": ["code generation"],
      "plan": ["reasoning", "long context"],
      "code-review": ["code generation", "reasoning"],
      "debug": ["reasoning", "code generation"]
    },
    "lastUpdated": "2026-04-14",
    "sourceUrl": "https://docs.anthropic.com/en/docs/about-claude/models"
  }
}
```

### Variable Reference Data Structure

```json
{
  "conventions": {
    "type": "string",
    "source": "repo .tamma/config.json conventions field",
    "description": "Language/framework coding conventions selected by the repo owner",
    "example": "Use TypeScript strict mode. Prefer async/await over .then()...",
    "usedBy": ["implement", "code-review", "refactor", "write-tests"]
  },
  "issue_body": {
    "type": "string",
    "source": "GitHub issue body (markdown)",
    "description": "The full body text of the GitHub issue being worked on",
    "example": "## Description\nAdd pagination to the /api/users endpoint...",
    "usedBy": ["context-scan", "plan", "triage", "summarize"]
  }
}
```

### Prompt Purpose Data Structure

Each role+action combination has a purpose entry in `packages/shared/src/data/prompt-purposes.json`:

```json
{
  "developer/implement": {
    "workflow": "SingleIssueCycleWorkflow",
    "activity": "ImplementActivity",
    "trigger": "After plan approval, the orchestrator dispatches the implement action",
    "expectedOutput": "git-ready code diff (file paths + content changes)",
    "outputFormat": "markdown code blocks with file paths",
    "downstreamConsumer": "Applied as a git commit by BranchCreationWorkflow, then validated by CI",
    "criticalVariables": ["issue_body", "plan", "conventions", "file_list"],
    "successCriteria": "Code compiles, tests pass, follows conventions, addresses all plan items"
  },
  "developer/plan": {
    "workflow": "PlanGenerationWorkflow",
    "activity": "GeneratePlanActivity",
    "trigger": "After context scan completes, before implementation",
    "expectedOutput": "structured development plan with file changes, approach, and risks",
    "outputFormat": "JSON matching DevelopmentPlan interface",
    "downstreamConsumer": "Parsed as JSON by PlanReviewWorkflow, displayed to user for approval",
    "criticalVariables": ["issue_body", "context", "conventions"],
    "successCriteria": "Plan is actionable, file list is accurate, risks identified"
  }
}
```

This data is used by the "Verify Prompt" feature to give the LLM reviewer full context about *why* this prompt exists and what happens to its output.

### API Endpoints Consumed

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/prompts/system` | GET | List all system default prompts |
| `GET /api/prompts/system/:role/:action` | GET | Get specific system default |
| `PUT /api/prompts/system/:role/:action` | PUT | Update system default |
| `DELETE /api/prompts/system/:role/:action` | DELETE | Reset to hardcoded default |
| `GET /api/convention-templates` | GET | List convention templates |
| `GET /api/convention-templates/:key` | GET | Get full convention template |
| `GET /api/v1/agents/config` | GET | Get agent config (to determine current LLM per role) |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/admin/PromptsPage.tsx` | Main prompts admin page with tabs |
| `packages/dashboard/src/components/prompts/PromptTable.tsx` | Filterable, searchable prompt table |
| `packages/dashboard/src/components/prompts/PromptEditDialog.tsx` | Edit view with template editor + context panels |
| `packages/dashboard/src/components/prompts/TemplateEditor.tsx` | CodeMirror 6 wrapper with custom syntax highlighting (variables, keywords, markdown), autocomplete for `{{` variables and keywords, hover tooltips |
| `packages/dashboard/src/components/prompts/template-language.ts` | CodeMirror language support: tokenizer for `{{variables}}`, markdown headers, keyword detection |
| `packages/dashboard/src/components/prompts/template-autocomplete.ts` | CodeMirror autocomplete source: variable completion on `{{`, keyword suggestions |
| `packages/dashboard/src/components/prompts/template-tooltips.ts` | CodeMirror hover tooltip plugin: shows variable/keyword info on hover |
| `packages/dashboard/src/components/prompts/PromptVerifyPanel.tsx` | Displays LLM review results: score, strengths, issues, suggestions with "Apply" buttons |
| `packages/dashboard/src/hooks/usePromptVerify.ts` | Async hook that calls POST /api/prompts/verify, manages loading/cache state |
| `packages/api/src/routes/prompts/prompt-verify.ts` | Endpoint that delegates to orchestrator LLM call with prompt-review action |
| `packages/dashboard/src/components/prompts/WikiDocPanel.tsx` | Wiki documentation panel (role + action docs) |
| `packages/dashboard/src/components/prompts/LlmInfoPanel.tsx` | LLM capability and benchmark panel |
| `packages/dashboard/src/components/prompts/VariableRefPanel.tsx` | Variable and keyword reference panel |
| `packages/dashboard/src/components/prompts/SystemPromptEditor.tsx` | System prompt inline editor |
| `packages/dashboard/src/components/prompts/ActionDefaultEditor.tsx` | Action default editor |
| `packages/dashboard/src/components/prompts/ConventionPreview.tsx` | Convention template preview + copy |
| `packages/shared/src/data/prompt-purposes.json` | Purpose context per role+action: workflow, activity, output format, downstream consumer, critical variables |
| `packages/dashboard/src/hooks/usePrompts.ts` | Data fetching hook for prompt API |
| `packages/dashboard/src/hooks/useWikiDocs.ts` | Wiki content fetching with session cache |
| `packages/shared/src/data/llm-profiles.json` | LLM capability data (all supported providers) |
| `packages/shared/src/data/prompt-variables.json` | Variable reference data |
| `packages/shared/src/data/prompt-keywords.json` | Prompt engineering keywords per action |
| `apps/wiki-site/public/content/prompts/index.md` | Prompt documentation index |
| `apps/wiki-site/public/content/prompts/role-developer.md` | Developer role wiki page |
| `apps/wiki-site/public/content/prompts/role-tester.md` | Tester role wiki page |
| `apps/wiki-site/public/content/prompts/role-security.md` | Security role wiki page |
| `apps/wiki-site/public/content/prompts/role-devops.md` | DevOps role wiki page |
| `apps/wiki-site/public/content/prompts/role-architect.md` | Architect role wiki page |
| `apps/wiki-site/public/content/prompts/role-product_owner.md` | Product Owner role wiki page |
| `apps/wiki-site/public/content/prompts/role-senior_developer.md` | Senior Developer role wiki page |
| `apps/wiki-site/public/content/prompts/role-tech_writer.md` | Tech Writer role wiki page |
| `apps/wiki-site/public/content/prompts/action-context-scan.md` | Context Scan action wiki page |
| `apps/wiki-site/public/content/prompts/action-plan.md` | Plan action wiki page |
| `apps/wiki-site/public/content/prompts/action-plan-review.md` | Plan Review action wiki page |
| `apps/wiki-site/public/content/prompts/action-implement.md` | Implement action wiki page |
| `apps/wiki-site/public/content/prompts/action-write-tests.md` | Write Tests action wiki page |
| `apps/wiki-site/public/content/prompts/action-refactor.md` | Refactor action wiki page |
| `apps/wiki-site/public/content/prompts/action-code-review.md` | Code Review action wiki page |
| `apps/wiki-site/public/content/prompts/action-triage.md` | Triage action wiki page |
| `apps/wiki-site/public/content/prompts/action-summarize.md` | Summarize action wiki page |
| `apps/wiki-site/public/content/prompts/action-debug.md` | Debug action wiki page |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PromptWikiRefreshWorkflow.cs` | Weekly wiki refresh Elsa workflow |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/routes.tsx` | Add route for `/admin/prompts` |
| `packages/dashboard/src/components/navigation/AdminNav.tsx` | Add "Prompts" nav link |

## Implementation Plan

### Phase 1: Data Files + Wiki Content (4h)

1. Create `llm-profiles.json` with profiles for: Anthropic Claude (Opus/Sonnet/Haiku), OpenAI (GPT-4o/o3/o1), Google Gemini, local LLMs. Data sourced from public model cards and benchmarks.
2. Create `prompt-variables.json` with all template variables, types, sources, descriptions, examples.
3. Create `prompt-keywords.json` with prompt engineering keywords per action.
4. Create all 18 wiki pages (8 roles + 10 actions) with initial documentation.
5. Create wiki index page mapping all 80 role+action combinations.

### Phase 2: Core Prompt Table + Edit View (10h)

1. `usePrompts` hook wrapping API calls
2. `PromptTable` with role/action filters, text search
3. `PromptEditDialog` (inline split view, not popup) with context panels
4. `TemplateEditor` — CodeMirror 6 integration:
   - Custom language support (`template-language.ts`): tokenizer that highlights `{{variables}}` in purple, markdown headers in bold, known keywords in blue, unknown variables in orange/red
   - Autocomplete (`template-autocomplete.ts`): typing `{{` opens a variable completion dropdown with name, type badge, and one-line description; keyword suggestions after 3+ chars
   - Hover tooltips (`template-tooltips.ts`): hovering a `{{variable}}` or keyword shows description, source, type, example value
   - Dependencies: `@codemirror/view`, `@codemirror/state`, `@codemirror/language`, `@codemirror/autocomplete`, `@codemirror/lang-markdown`
5. `SystemPromptEditor` for role preambles
6. `ActionDefaultEditor` for action templates
7. `ConventionPreview` with copy-to-clipboard
8. Route and navigation wiring

### Phase 3: Context Panels + Verification (12h)

1. `WikiDocPanel` — fetches and displays role/action docs from wiki
2. `LlmInfoPanel` — shows current LLM for role, strengths, benchmarks, action affinity highlighting
3. `VariableRefPanel` — variable list with click-to-insert, keyword reference
4. Template variable highlighting in editor
5. `useWikiDocs` hook with session caching
6. `PromptVerifyPanel` — "Verify Prompt" button, async LLM review call, displays score/strengths/issues/suggestions
7. `prompt-verify.ts` API endpoint — receives template + role + action + LLM profile, calls orchestrator LLM with `prompt-review` meta-prompt, returns structured feedback
8. "Apply suggestion" buttons — show diff preview, patch template section on accept

### Phase 4: Weekly Refresh Workflow (6h)

1. `PromptWikiRefreshWorkflow.cs` — Elsa workflow with weekly cron
2. Wiki coverage scan (check which pages exist)
3. LLM-powered draft generation for missing docs
4. Stale doc detection (prompt changed since wiki last updated)
5. Summary report to admin notifications
6. LLM profile data refresh from provider model cards

## Testing Strategy

### Unit Tests

1. `PromptTable` renders 80 rows from mock data
2. Role filter reduces displayed rows correctly
3. Action filter reduces displayed rows correctly
4. Search filter matches against template content
5. `PromptEditDialog` displays template, variables, system prompt, tools, max tokens
6. `TemplateEditor` highlights `{{variables}}` in purple inline
7. `TemplateEditor` highlights known keywords in blue inline
8. `TemplateEditor` marks unknown `{{invalid_var}}` in orange/red
9. Typing `{{` triggers autocomplete dropdown with variable names and descriptions
10. Selecting from autocomplete inserts full `{{variable}}` token
11. Hovering a `{{variable}}` shows tooltip with description, source, type, example
12. Save calls `PUT /api/prompts/system/:role/:action` with correct body
13. Reset calls `DELETE` after confirmation
14. "Verify Prompt" calls `POST /api/prompts/verify` with template, role, action, LLM profile
15. Verify result displays score, color-coded issues, and suggestion "Apply" buttons
16. "Apply" button shows diff preview and patches template on accept
17. Verify result is cached — re-verify only if template text changes
9. `WikiDocPanel` renders role + action documentation from mock wiki data
10. `WikiDocPanel` shows placeholder when wiki page missing
11. `LlmInfoPanel` displays LLM strengths and highlights action-relevant ones
12. `VariableRefPanel` lists variables with descriptions
13. Click-to-insert adds variable at cursor position
14. Non-admin users see 403 message
15. Convention template preview shows correct content

### Integration Tests

16. Full edit flow: load page → click row → edit template → save → verify updated
17. Reset flow: edit → reset → verify original restored
18. Wiki docs load from wiki site content API

## Dependencies

- **Story 27-3** (Prompt Store API Endpoints) — API endpoints must exist
- **Story 16.3** (Admin Dashboard) — admin panel framework
- **Story 16.5** (RBAC) — platform admin role check
- **Story 9-1** (Agent Config API) — for current LLM per role lookup

## Estimated Effort

| Task | Hours |
|------|-------|
| LLM profiles + variable/keyword reference data | 4 |
| Wiki pages (8 roles + 10 actions + index) | 4 |
| Prompt table + filters + search | 3 |
| Edit view + CodeMirror template editor (highlighting, autocomplete, tooltips) | 8 |
| Wiki documentation panel | 3 |
| LLM info panel with action affinity | 3 |
| Variable reference panel with click-to-insert | 3 |
| Convention preview | 1 |
| Weekly refresh Elsa workflow | 6 |
| Route/nav wiring + RBAC | 1 |
| Prompt verification panel + API endpoint | 4 |
| Unit tests (24 tests) | 4 |
| Integration tests (3 tests) | 1 |
| **Total** | **48 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
| 2026-04-14 | 2.0 | Added wiki integration, LLM context panel, variable/keyword reference, weekly refresh workflow | Architecture Team |
