import { useCallback, useMemo } from 'react';
import {
  ReactFlow,
  Background,
  type Node,
  type Edge,
  type NodeTypes,
  Position,
  Handle,
  BackgroundVariant,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';

// --- Custom Node Types ---

function ProcessNode({ data }: { data: { label: string; description?: string } }) {
  return (
    <div className="bg-zinc-800 border border-zinc-600 rounded-lg px-4 py-2.5 min-w-[140px] max-w-[200px] shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-zinc-500 !w-2 !h-2 !border-0" />
      <div className="text-[12px] font-medium text-zinc-200 text-center leading-tight">{data.label}</div>
      {data.description && (
        <div className="text-[10px] text-zinc-500 text-center mt-1 leading-tight">{data.description}</div>
      )}
      <Handle type="source" position={Position.Bottom} className="!bg-zinc-500 !w-2 !h-2 !border-0" />
    </div>
  );
}

function DecisionNode({ data }: { data: { label: string } }) {
  return (
    <div className="relative">
      <Handle type="target" position={Position.Top} className="!bg-amber-500 !w-2 !h-2 !border-0" />
      <div className="bg-amber-500/10 border border-amber-500/40 rounded-lg px-4 py-2.5 min-w-[120px] max-w-[180px] shadow-lg shadow-black/30"
        style={{ clipPath: 'polygon(50% 0%, 100% 50%, 50% 100%, 0% 50%)' , padding: '20px 30px' }}>
      </div>
      <div className="absolute inset-0 flex items-center justify-center">
        <span className="text-[11px] font-medium text-amber-300 text-center leading-tight">{data.label}</span>
      </div>
      <Handle type="source" position={Position.Bottom} className="!bg-amber-500 !w-2 !h-2 !border-0" />
      <Handle type="source" position={Position.Right} id="right" className="!bg-amber-500 !w-2 !h-2 !border-0" />
      <Handle type="source" position={Position.Left} id="left" className="!bg-amber-500 !w-2 !h-2 !border-0" />
    </div>
  );
}

function StartNode({ data }: { data: { label: string } }) {
  return (
    <div className="bg-emerald-500/15 border border-emerald-500/40 rounded-full px-5 py-2 shadow-lg shadow-black/30">
      <div className="text-[12px] font-semibold text-emerald-400 text-center">{data.label}</div>
      <Handle type="source" position={Position.Bottom} className="!bg-emerald-500 !w-2 !h-2 !border-0" />
    </div>
  );
}

function EndNode({ data }: { data: { label: string } }) {
  return (
    <div className="bg-red-500/10 border border-red-500/30 rounded-full px-5 py-2 shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-red-400 !w-2 !h-2 !border-0" />
      <div className="text-[12px] font-semibold text-red-400 text-center">{data.label}</div>
    </div>
  );
}

function SubWorkflowNode({ data }: { data: { label: string; description?: string } }) {
  return (
    <div className="bg-blue-500/10 border-2 border-blue-500/30 border-dashed rounded-lg px-4 py-2.5 min-w-[150px] max-w-[200px] shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-blue-400 !w-2 !h-2 !border-0" />
      <div className="text-[10px] text-blue-400/60 uppercase tracking-wider mb-0.5">sub-workflow</div>
      <div className="text-[12px] font-medium text-blue-300 text-center leading-tight">{data.label}</div>
      {data.description && (
        <div className="text-[10px] text-blue-400/50 text-center mt-1">{data.description}</div>
      )}
      <Handle type="source" position={Position.Bottom} className="!bg-blue-400 !w-2 !h-2 !border-0" />
    </div>
  );
}

function ParallelNode({ data }: { data: { label: string; items: string[] } }) {
  return (
    <div className="bg-purple-500/10 border border-purple-500/30 rounded-lg px-4 py-3 min-w-[180px] shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-purple-400 !w-2 !h-2 !border-0" />
      <div className="text-[10px] text-purple-400/60 uppercase tracking-wider mb-1.5">parallel</div>
      <div className="text-[12px] font-medium text-purple-300 mb-2">{data.label}</div>
      <div className="flex flex-wrap gap-1">
        {data.items.map((item, i) => (
          <span key={i} className="text-[10px] bg-purple-500/15 border border-purple-500/20 rounded px-2 py-0.5 text-purple-300">
            {item}
          </span>
        ))}
      </div>
      <Handle type="source" position={Position.Bottom} className="!bg-purple-400 !w-2 !h-2 !border-0" />
    </div>
  );
}

const nodeTypes: NodeTypes = {
  process: ProcessNode,
  decision: DecisionNode,
  start: StartNode,
  end: EndNode,
  subworkflow: SubWorkflowNode,
  parallel: ParallelNode,
};

// --- Workflow Definitions ---

export interface WorkflowDef {
  nodes: Node[];
  edges: Edge[];
}

const edgeDefaults = {
  style: { stroke: '#52525b', strokeWidth: 1.5 },
  labelStyle: { fill: '#a1a1aa', fontSize: 10, fontFamily: 'ui-sans-serif, system-ui' },
  labelBgStyle: { fill: '#18181b', fillOpacity: 0.9 },
  labelBgPadding: [4, 6] as [number, number],
  labelBgBorderRadius: 4,
};

function e(source: string, target: string, label?: string, sourceHandle?: string): Edge {
  return {
    id: `${source}-${target}${sourceHandle || ''}`,
    source,
    target,
    label,
    sourceHandle,
    type: 'smoothstep',
    ...edgeDefaults,
  };
}

function n(id: string, label: string, x: number, y: number, type = 'process', extra?: Record<string, unknown>): Node {
  return { id, position: { x, y }, data: { label, ...extra }, type };
}

// --- Predefined Workflow Diagrams ---

const WORKFLOW_DIAGRAMS: Record<string, WorkflowDef> = {
  'single-issue-cycle': {
    nodes: [
      n('start', 'Start', 280, 0, 'start'),
      n('select', 'Select Issue', 280, 80),
      n('context', 'Gather Context', 280, 160, 'subworkflow'),
      n('plan', 'Generate Plan', 280, 250),
      n('approve', 'Plan Approved?', 280, 340, 'decision'),
      n('branch', 'Create Branch', 280, 440),
      n('tdd', 'TDD Cycle', 280, 530, 'subworkflow'),
      n('ci', 'Run CI', 280, 620, 'subworkflow'),
      n('cipass', 'CI Pass?', 280, 710, 'decision'),
      n('pr', 'Create PR', 280, 810),
      n('review', 'Code Review', 280, 900, 'subworkflow'),
      n('merge', 'Merge', 280, 990),
      n('done', 'Done', 280, 1070, 'end'),
      n('reject', 'Rejected', 500, 340, 'end'),
      n('debug', 'Debug Retry', 500, 710, 'subworkflow'),
    ],
    edges: [
      e('start', 'select'), e('select', 'context'), e('context', 'plan'),
      e('plan', 'approve'), e('approve', 'branch', 'Yes'), e('approve', 'reject', 'No', 'right'),
      e('branch', 'tdd'), e('tdd', 'ci'), e('ci', 'cipass'),
      e('cipass', 'pr', 'Pass'), e('cipass', 'debug', 'Fail', 'right'),
      e('debug', 'ci'), e('pr', 'review'), e('review', 'merge'), e('merge', 'done'),
    ],
  },
  'tdd-cycle': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('write-tests', 'Write Tests', 250, 80, 'process', { description: 'LLM generates failing tests' }),
      n('run-red', 'Run Tests', 250, 170),
      n('tests-fail', 'Tests Fail?', 250, 260, 'decision'),
      n('write-impl', 'Write Implementation', 250, 370, 'process', { description: 'Minimum code to pass' }),
      n('run-green', 'Run Tests', 250, 460),
      n('tests-pass', 'Tests Pass?', 250, 550, 'decision'),
      n('analyze', 'Analyze Code', 250, 660),
      n('refactor-needed', 'Refactor?', 250, 750, 'decision'),
      n('refactor', 'Apply Refactoring', 250, 850),
      n('verify', 'Verify Tests', 250, 940),
      n('commit', 'Commit', 250, 1030),
      n('done', 'Done', 250, 1110, 'end'),
      n('rewrite', 'Rewrite Tests', 500, 260),
      n('debug', 'Debug', 500, 550, 'subworkflow'),
    ],
    edges: [
      e('start', 'write-tests'), e('write-tests', 'run-red'), e('run-red', 'tests-fail'),
      e('tests-fail', 'write-impl', 'Yes'), e('tests-fail', 'rewrite', 'No', 'right'),
      e('rewrite', 'write-tests'),
      e('write-impl', 'run-green'), e('run-green', 'tests-pass'),
      e('tests-pass', 'analyze', 'Yes'), e('tests-pass', 'debug', 'No', 'right'),
      e('debug', 'write-impl'),
      e('analyze', 'refactor-needed'),
      e('refactor-needed', 'refactor', 'Yes'), e('refactor-needed', 'commit', 'No'),
      e('refactor', 'verify'), e('verify', 'commit'), e('commit', 'done'),
    ],
  },
  'llm-call': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('budget', 'Check Budget', 250, 80),
      n('resolve', 'Resolve Agent Config', 250, 170),
      n('chain', 'Provider Chain', 250, 260, 'parallel', { items: ['Claude', 'OpenAI', 'OpenRouter', 'Local'] }),
      n('circuit', 'Circuit Open?', 250, 380, 'decision'),
      n('call', 'Call LLM', 250, 480),
      n('tools', 'Tool Loop', 250, 570, 'decision'),
      n('exec', 'Execute Tools', 250, 670, 'parallel', { items: ['FileRead', 'Shell', 'Search', 'Git'] }),
      n('compact', 'Compact Context', 250, 790),
      n('done', 'Return Result', 250, 880, 'end'),
      n('skip', 'Next Provider', 500, 380),
      n('retry', 'Retry w/ Backoff', 500, 480),
    ],
    edges: [
      e('start', 'budget'), e('budget', 'resolve'), e('resolve', 'chain'),
      e('chain', 'circuit'),
      e('circuit', 'call', 'Closed'), e('circuit', 'skip', 'Open', 'right'),
      e('skip', 'chain'),
      e('call', 'tools'),
      e('tools', 'exec', 'Has tools'), e('tools', 'done', 'No tools'),
      e('exec', 'compact'), e('compact', 'call'),
      e('call', 'retry'),
      e('retry', 'call'),
    ],
  },
  'mentorship': {
    nodes: [
      n('start', 'Start Session', 220, 0, 'start'),
      n('init', 'Initialize Story', 220, 80),
      n('validate', 'Validate Story', 220, 160),
      n('assess', 'Assess Skills', 220, 250, 'subworkflow'),
      n('plan', 'Plan Decomposition', 220, 350),
      n('approve', 'Plan OK?', 220, 440, 'decision'),
      n('impl', 'TDD Implementation', 220, 550, 'subworkflow'),
      n('monitor', 'Monitor Progress', 220, 650),
      n('blocked', 'Blocked?', 220, 740, 'decision'),
      n('quality', 'Quality Gate', 220, 850, 'subworkflow'),
      n('review', 'Code Review', 220, 950, 'subworkflow'),
      n('merge', 'Merge & Report', 220, 1050),
      n('done', 'Complete', 220, 1140, 'end'),
      n('diagnose', 'Diagnose Blocker', 480, 740, 'subworkflow'),
      n('escalate', 'Escalate', 480, 850, 'end'),
      n('replan', 'Replan', 480, 440),
    ],
    edges: [
      e('start', 'init'), e('init', 'validate'), e('validate', 'assess'),
      e('assess', 'plan'), e('plan', 'approve'),
      e('approve', 'impl', 'Yes'), e('approve', 'replan', 'No', 'right'),
      e('replan', 'plan'),
      e('impl', 'monitor'), e('monitor', 'blocked'),
      e('blocked', 'quality', 'No'), e('blocked', 'diagnose', 'Yes', 'right'),
      e('diagnose', 'escalate', 'Unresolved'),
      e('diagnose', 'impl', 'Resolved'),
      e('quality', 'review'), e('review', 'merge'), e('merge', 'done'),
    ],
  },
  'code-review': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('create-pr', 'Create PR', 250, 80),
      n('request', 'Request Review', 250, 170),
      n('wait', 'Wait for Review', 250, 260, 'process', { description: 'Bookmark (24h timeout)' }),
      n('result', 'Review Result?', 250, 360, 'decision'),
      n('merge', 'Merge & Complete', 250, 470),
      n('done', 'Done', 250, 550, 'end'),
      n('guidance', 'Deliver Guidance', 500, 360),
      n('fix', 'Wait for Fixes', 500, 460, 'process', { description: 'Bookmark' }),
      n('max', 'Max Iterations?', 500, 560, 'decision'),
      n('rerequest', 'Re-request Review', 500, 260),
      n('escalate', 'Escalate', 700, 560, 'end'),
    ],
    edges: [
      e('start', 'create-pr'), e('create-pr', 'request'), e('request', 'wait'),
      e('wait', 'result'),
      e('result', 'merge', 'Approved'), e('result', 'guidance', 'Changes', 'right'),
      e('guidance', 'fix'), e('fix', 'max'),
      e('max', 'rerequest', 'No'), e('max', 'escalate', 'Yes', 'right'),
      e('rerequest', 'wait'),
      e('merge', 'done'),
    ],
  },
  'testing': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('trigger', 'Trigger CI', 250, 80),
      n('wait', 'Wait for Results', 250, 170, 'process', { description: 'Bookmark' }),
      n('evaluate', 'Evaluate', 250, 260, 'decision'),
      n('checks', 'Quality Checks', 250, 370, 'parallel', { items: ['Coverage', 'Linting', 'Security'] }),
      n('report', 'Generate Report', 250, 490),
      n('pass', 'Pass', 250, 580, 'end'),
      n('fix', 'Auto-Fix', 500, 260),
      n('maxretry', 'Max Retries?', 500, 370, 'decision'),
      n('fail', 'Fail', 500, 490, 'end'),
    ],
    edges: [
      e('start', 'trigger'), e('trigger', 'wait'), e('wait', 'evaluate'),
      e('evaluate', 'checks', 'Pass/Minor'),
      e('evaluate', 'fix', 'Major', 'right'),
      e('fix', 'maxretry'),
      e('maxretry', 'trigger', 'No'), e('maxretry', 'fail', 'Yes', 'right'),
      e('checks', 'report'), e('report', 'pass'),
      e('evaluate', 'checks', 'Critical'),
    ],
  },
  'debugging': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('classify', 'Classify Context', 250, 80, 'process', { description: 'TDD/Runtime/Bug' }),
      n('collect', 'Collect Context', 250, 180, 'parallel', { items: ['Errors', 'Code', 'Git', 'Tests', 'Repro'] }),
      n('diagnose', 'AI Diagnosis', 250, 310),
      n('hypothesis', 'Select Hypothesis', 250, 400),
      n('has-hyp', 'Has Hypothesis?', 250, 490, 'decision'),
      n('fix', 'Apply Fix', 250, 590, 'subworkflow'),
      n('test', 'Run Tests', 250, 680, 'subworkflow'),
      n('pass', 'Tests Pass?', 250, 770, 'decision'),
      n('record', 'Record Resolution', 250, 870),
      n('done', 'Resolved', 250, 950, 'end'),
      n('refine', 'Refine Hypothesis', 500, 770),
      n('escalate', 'Escalate', 500, 490, 'end'),
    ],
    edges: [
      e('start', 'classify'), e('classify', 'collect'), e('collect', 'diagnose'),
      e('diagnose', 'hypothesis'), e('hypothesis', 'has-hyp'),
      e('has-hyp', 'fix', 'Yes'), e('has-hyp', 'escalate', 'No', 'right'),
      e('fix', 'test'), e('test', 'pass'),
      e('pass', 'record', 'Yes'), e('pass', 'refine', 'No', 'right'),
      e('refine', 'hypothesis'),
      e('record', 'done'),
    ],
  },
  'blocker-diagnosis': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('signals', 'Collect Signals', 250, 80, 'parallel', { items: ['Git', 'CI', 'Inactivity', 'Comms'] }),
      n('ai', 'AI Diagnosis', 250, 210),
      n('classify', 'Classify Blocker', 250, 300, 'process', { description: '8 categories' }),
      n('hint', 'Level 1: Hint', 250, 400, 'process', { description: 'Socratic question' }),
      n('progress1', 'Progress?', 250, 490, 'decision'),
      n('guidance', 'Level 2: Guidance', 250, 590, 'process', { description: 'Direct guidance' }),
      n('progress2', 'Progress?', 250, 680, 'decision'),
      n('assist', 'Level 3: Assistance', 250, 780, 'process', { description: 'Code examples' }),
      n('progress3', 'Progress?', 250, 870, 'decision'),
      n('escalate', 'Level 4: Escalate', 250, 960),
      n('resolved', 'Resolved', 500, 490, 'end'),
    ],
    edges: [
      e('start', 'signals'), e('signals', 'ai'), e('ai', 'classify'),
      e('classify', 'hint'), e('hint', 'progress1'),
      e('progress1', 'resolved', 'Yes', 'right'), e('progress1', 'guidance', 'No'),
      e('guidance', 'progress2'),
      e('progress2', 'resolved', 'Yes', 'right'), e('progress2', 'assist', 'No'),
      e('assist', 'progress3'),
      e('progress3', 'resolved', 'Yes', 'right'), e('progress3', 'escalate', 'No'),
    ],
  },
  'context-gathering': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('phase1', 'Phase 1: Parallel Fetch', 250, 90, 'parallel', { items: ['Story', 'Commits', 'Tests', 'History'] }),
      n('check', 'Story Found?', 250, 220, 'decision'),
      n('phase2', 'Phase 2: Dependent Fetch', 250, 320, 'parallel', { items: ['File Contents', 'Similar Patterns'] }),
      n('assemble', 'Assemble Context', 250, 440),
      n('budget', 'Apply Budget', 250, 530, 'process', { description: 'Priority-based trimming' }),
      n('done', 'Return Context', 250, 620, 'end'),
      n('abort', 'Abort', 500, 220, 'end'),
    ],
    edges: [
      e('start', 'phase1'), e('phase1', 'check'),
      e('check', 'phase2', 'Yes'), e('check', 'abort', 'No', 'right'),
      e('phase2', 'assemble'), e('assemble', 'budget'), e('budget', 'done'),
    ],
  },
};

// Fallback: generate a simple linear diagram from flow steps
function generateFallback(steps: FlowStep[]): WorkflowDef {
  const nodes: Node[] = steps.map((step, i) => {
    const type = step.type === 'decision' ? 'decision'
      : step.type === 'start' ? 'start'
      : step.type === 'terminal' ? 'end'
      : 'process';
    return n(`s${i}`, step.label, 250, i * 100, type);
  });
  const edges: Edge[] = steps.slice(1).map((_, i) => e(`s${i}`, `s${i + 1}`));
  return { nodes, edges };
}

// --- Main Component ---

interface Props {
  slug: string;
  flowSteps?: FlowStep[];
}

interface FlowStep {
  id: string;
  label: string;
  type: 'process' | 'decision' | 'terminal' | 'start';
}

export default function WorkflowDiagram({ slug, flowSteps }: Props) {
  const diagram = useMemo(() => {
    if (WORKFLOW_DIAGRAMS[slug]) return WORKFLOW_DIAGRAMS[slug];
    if (flowSteps && flowSteps.length > 2) return generateFallback(flowSteps);
    return null;
  }, [slug, flowSteps]);

  if (!diagram) return null;

  const maxY = Math.max(...diagram.nodes.map(n => n.position.y));
  const height = Math.min(maxY + 120, 900);

  return (
    <div className="my-6">
      <div
        className="bg-[#0c0c0e] border border-zinc-800 rounded-xl overflow-hidden"
        style={{ height: `${height}px` }}
      >
        <ReactFlow
          nodes={diagram.nodes}
          edges={diagram.edges}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.2 }}
          minZoom={0.3}
          maxZoom={1.5}
          panOnDrag
          zoomOnScroll
          nodesDraggable={false}
          nodesConnectable={false}
          proOptions={{ hideAttribution: true }}
          defaultEdgeOptions={{ type: 'smoothstep', ...edgeDefaults }}
        >
          <Background color="#27272a" gap={20} variant={BackgroundVariant.Dots} />
        </ReactFlow>
      </div>
    </div>
  );
}

export { WORKFLOW_DIAGRAMS };
export type { FlowStep };
