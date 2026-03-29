# Story 19-1: Tamma Agent GitHub Actions Workflow Template

Status: ready-for-dev

## Story

As a **user installing the Tamma GitHub App**,
I want a ready-made `.github/workflows/tamma-agent.yml` that I add to my repository,
so that Tamma Cloud can dispatch agent runs on my own GitHub Actions runners, and the agent (Claude Code or similar) executes within my repo's security context using my own API keys.

## Acceptance Criteria

1. A reusable workflow file `tamma-agent.yml` exists that users copy into `.github/workflows/`
2. The workflow triggers only on `workflow_dispatch` with well-defined inputs:
   - `issue_number` (string): The GitHub issue number to work on
   - `task` (string): The task type (`implement`, `fix`, `debug`, `review`, `test`)
   - `plan_json` (string): Serialized development plan from Tamma's plan generation step
   - `branch_name` (string): The branch to check out and work on
   - `tamma_session_id` (string): Correlation ID for Tamma event tracking
   - `agent_provider` (string, default: `claude-code`): Which agent to invoke
   - `agent_config_json` (string, optional): Additional agent configuration
3. The workflow checks out the target branch, installs the agent CLI, and runs it with the provided plan
4. Agent API keys come from GitHub Actions secrets (e.g., `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`) -- Tamma never provides or sees these keys
5. The workflow creates a `.tamma/result.json` artifact containing structured output:
   - `success` (boolean)
   - `files_changed` (string[])
   - `pr_number` (number | null)
   - `commit_sha` (string)
   - `error_message` (string | null)
   - `agent_log_summary` (string)
   - `tokens_used` (number)
   - `duration_seconds` (number)
6. The workflow posts a status comment on the issue with execution summary
7. The workflow supports configurable runner labels (`runs-on` parameterizable for self-hosted runners)
8. The workflow has a configurable timeout (default: 30 minutes)
9. The workflow is idempotent -- running it twice with the same inputs does not create duplicate PRs
10. Clear documentation in the workflow file itself (YAML comments) explaining each step and required secrets

## Technical Context

### Workflow Structure

```yaml
name: Tamma Agent
on:
  workflow_dispatch:
    inputs:
      issue_number:
        description: 'GitHub issue number'
        required: true
        type: string
      task:
        description: 'Task type: implement, fix, debug, review, test'
        required: true
        type: string
      plan_json:
        description: 'Development plan JSON from Tamma'
        required: true
        type: string
      branch_name:
        description: 'Branch to check out and work on'
        required: true
        type: string
      tamma_session_id:
        description: 'Tamma session ID for correlation'
        required: true
        type: string
      agent_provider:
        description: 'Agent to use (claude-code, aider, etc.)'
        required: false
        type: string
        default: 'claude-code'
      agent_config_json:
        description: 'Additional agent config JSON'
        required: false
        type: string
        default: '{}'

jobs:
  tamma-agent:
    runs-on: ubuntu-latest
    timeout-minutes: 30
    permissions:
      contents: write
      pull-requests: write
      issues: write
    steps:
      - name: Checkout branch
      - name: Setup agent environment
      - name: Write plan file
      - name: Run agent
      - name: Collect results
      - name: Upload result artifact
      - name: Post issue comment
```

### Agent Invocation Pattern

For Claude Code (primary agent):
```bash
# Install Claude Code CLI
npm install -g @anthropic-ai/claude-code

# Run in non-interactive/headless mode with the plan
claude-code --headless \
  --plan-file .tamma/plan.json \
  --output-file .tamma/result.json \
  --timeout 1800 \
  --api-key "$ANTHROPIC_API_KEY"
```

For extensibility, the workflow uses a dispatch pattern:
```yaml
- name: Run agent
  run: |
    case "${{ inputs.agent_provider }}" in
      claude-code) ./scripts/run-claude-code.sh ;;
      aider)       ./scripts/run-aider.sh ;;
      *)           echo "Unknown agent: ${{ inputs.agent_provider }}"; exit 1 ;;
    esac
```

### Result Artifact Schema

```typescript
interface AgentResult {
  success: boolean;
  task: string;
  issue_number: number;
  branch_name: string;
  tamma_session_id: string;
  files_changed: string[];
  pr_number: number | null;
  commit_sha: string;
  error_message: string | null;
  agent_log_summary: string;
  tokens_used: number;
  duration_seconds: number;
  agent_provider: string;
  agent_version: string;
}
```

### Security Considerations

- The workflow runs with the minimum permissions needed (`contents: write`, `pull-requests: write`, `issues: write`)
- API keys are GitHub Actions secrets -- never logged, never sent to Tamma
- The `plan_json` input is the only data Tamma sends; it contains no user code, only task descriptions and file paths
- The result artifact contains metadata only -- no source code content
- The workflow should use `actions/upload-artifact@v4` with a short retention period (1 day)

### CLAUDE.md Integration

The workflow writes a `.tamma/INSTRUCTIONS.md` file before invoking the agent, containing:
- The development plan
- Branch context (base branch, issue details)
- Repository conventions (read from existing CLAUDE.md if present)
- Output format instructions

This ensures the agent follows Tamma's conventions regardless of which agent is used.

## Implementation Notes

### Files to Create

- `templates/github-actions/tamma-agent.yml` -- The reusable workflow template
- `templates/github-actions/scripts/run-claude-code.sh` -- Claude Code agent runner script
- `templates/github-actions/scripts/collect-results.sh` -- Result collection script
- `docs/guides/github-actions-setup.md` -- User-facing setup documentation (if requested)

### Agent Provider Extensibility

The template should support multiple agents through a simple dispatch pattern. Each agent has:
1. An installation step (install CLI/package)
2. A run step (execute with plan)
3. A result collection step (parse output into standard format)

Claude Code is the primary agent. Others (Aider, Codex CLI, etc.) can be added later.

### Idempotency

The workflow must handle re-runs gracefully:
- Before creating a PR, check if one already exists for the branch
- Before committing, check if the branch already has the expected changes
- Use the `tamma_session_id` to correlate with previous runs

### Error Handling

- If the agent fails, the workflow must still upload a result artifact with `success: false` and the error message
- If the workflow times out, GitHub Actions provides a cancellation signal -- the cleanup step should still run
- Network failures during agent execution should be captured in the result

## Dependencies

- Claude Code CLI must support a headless/non-interactive mode with plan input (verify latest docs)
- GitHub Actions `workflow_dispatch` API supports string inputs (confirmed)
- GitHub Actions artifact upload/download API (v4)

## Estimated Effort

**Size**: M (Medium)
- Workflow YAML authoring and testing: 2 days
- Agent runner scripts: 1 day
- Result collection and artifact schema: 1 day
- Testing on real GitHub Actions runner: 1 day
- Documentation: 0.5 day

**Total**: ~5.5 days

## Testing Strategy

### Unit Tests
- Validate result artifact JSON schema
- Test idempotency logic (mock: PR already exists)
- Test error handling (mock: agent fails, timeout)

### Integration Tests
- Dispatch workflow on a test repository via GitHub API
- Verify agent runs and produces result artifact
- Verify issue comment is posted
- Test with different agent providers (Claude Code, mock agent)

### Manual Validation
- Install template on `tamma-test-github` repo
- Dispatch via GitHub API with a real issue
- Verify end-to-end: checkout, agent run, PR created, artifact uploaded
