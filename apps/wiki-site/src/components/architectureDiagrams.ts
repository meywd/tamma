import type { WorkflowMetadata } from '@tamma/workflow-viewer';

/**
 * Hand-authored architecture illustrations for the Architecture page.
 *
 * Unlike the workflow diagrams (generated from the C# Elsa source), these two
 * are conceptual overviews that don't correspond to a single Elsa workflow, so
 * they live here as static {@link WorkflowMetadata} and are passed straight to
 * <WorkflowViewer />. Keeping them in the same metadata shape means they reuse
 * the exact same renderer (kind-coding, pan/zoom, click-to-detail).
 */

const ARCHITECTURE_FLOW: WorkflowMetadata = {
  id: 'architecture-flow',
  name: 'Autonomous Development Flow',
  description: 'How a GitHub/CLI/API trigger flows through the orchestrator into the autonomous dev pipeline and supporting infrastructure.',
  nodes: [
    node('gh', 'GitHub Webhook', 'api-call', { isStart: true, description: 'Inbound webhook trigger.' }),
    node('cli', 'CLI Command', 'api-call', { isStart: true, description: 'Local CLI trigger.' }),
    node('api', 'REST API', 'api-call', { isStart: true, description: 'HTTP API trigger.' }),
    node('adl', 'ADL Orchestrator', 'dispatch-subworkflow', { subWorkflowId: 'adl-orchestrator', description: 'Top-level orchestration loop.' }),
    node('sic', 'Single Issue Cycle', 'dispatch-subworkflow', { subWorkflowId: 'single-issue-cycle', description: 'Full per-issue dev cycle.' }),
    node('pg', 'Plan Generation', 'dispatch-subworkflow', { subWorkflowId: 'plan-generation' }),
    node('tdd', 'TDD Cycle', 'dispatch-subworkflow', { subWorkflowId: 'tdd-cycle' }),
    node('cr', 'Code Review', 'dispatch-subworkflow', { subWorkflowId: 'code-review' }),
    node('merge', 'Merge', 'activity', { description: 'Merge PR and clean up branch.' }),
    node('chain', 'Provider Chain', 'api-call', { description: 'Multi-provider LLM fallback chain (Claude / OpenAI / OpenRouter / Local).' }),
    node('cb', 'Circuit Breaker', 'decision', { outcomes: ['Closed', 'Open'] }),
    node('github', 'GitHub API', 'api-call'),
    node('pg_db', 'PostgreSQL', 'activity', { description: 'Event store + task queue.' }),
    node('rmq', 'RabbitMQ', 'activity', { description: 'Work dispatch.' }),
    node('chroma', 'ChromaDB', 'activity', { description: 'Vector store for context.' }),
    node('done', 'Done', 'terminal'),
  ],
  edges: [
    { from: 'gh', to: 'adl' }, { from: 'cli', to: 'adl' }, { from: 'api', to: 'adl' },
    { from: 'adl', to: 'sic' }, { from: 'sic', to: 'pg' }, { from: 'pg', to: 'tdd' },
    { from: 'tdd', to: 'cr' }, { from: 'cr', to: 'merge' }, { from: 'merge', to: 'done' },
    { from: 'pg', to: 'chain' }, { from: 'tdd', to: 'chain' },
    { from: 'chain', to: 'cb' },
    { from: 'merge', to: 'github' },
    { from: 'adl', to: 'pg_db' }, { from: 'adl', to: 'rmq' },
    { from: 'tdd', to: 'chroma' },
  ],
};

const SECURITY_PIPELINE: WorkflowMetadata = {
  id: 'security-pipeline',
  name: 'Security Pipeline',
  description: 'Content sanitization, prompt hardening, tool validation and output redaction around every LLM call.',
  nodes: [
    node('input', 'User/LLM Input', 'api-call', { isStart: true }),
    node('sanitize', 'Content Sanitizer', 'activity', { description: 'HTML strip, zero-width char removal.' }),
    node('harden', 'Prompt Hardening', 'activity', { description: 'Anti-extraction preamble.' }),
    node('llm', 'LLM Call', 'dispatch-subworkflow', { subWorkflowId: 'llm-call' }),
    node('validate', 'Tool Validator', 'activity', { description: 'Allowlist + schema check.' }),
    node('gate', 'Action Gate', 'gate', { outcomes: ['Allowed', 'Denied'] }),
    node('exec', 'Tool Executor', 'activity'),
    node('redact', 'Redact Secrets', 'activity', { description: '10 secret patterns redacted.' }),
    node('output', 'Output Validator', 'activity'),
    node('clean', 'Clean Output', 'terminal'),
    node('block', 'Blocked', 'terminal'),
  ],
  edges: [
    { from: 'input', to: 'sanitize' }, { from: 'sanitize', to: 'harden' }, { from: 'harden', to: 'llm' },
    { from: 'llm', to: 'validate' }, { from: 'validate', to: 'gate' },
    { from: 'gate', to: 'exec', label: 'Allowed' }, { from: 'gate', to: 'block', label: 'Denied' },
    { from: 'exec', to: 'redact' }, { from: 'redact', to: 'output' }, { from: 'output', to: 'clean' },
  ],
};

export const ARCHITECTURE_DIAGRAMS: Record<string, WorkflowMetadata> = {
  'architecture-flow': ARCHITECTURE_FLOW,
  'security-pipeline': SECURITY_PIPELINE,
};

type NodeKind = WorkflowMetadata['nodes'][number]['kind'];

function node(
  id: string,
  name: string,
  kind: NodeKind,
  extra: Partial<WorkflowMetadata['nodes'][number]> = {},
): WorkflowMetadata['nodes'][number] {
  return {
    id,
    name,
    className: name,
    kind,
    description: '',
    inputs: [],
    outputs: [],
    outcomes: [],
    interactions: [],
    ...extra,
  };
}
