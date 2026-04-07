import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Node,
  type Edge,
  type NodeTypes,
  Position,
  Handle,
  BackgroundVariant,
  useNodesState,
  useEdgesState,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import ELK from 'elkjs/lib/elk.bundled.js';

// --- Custom Node Types ---

function ProcessNode({ data }: { data: { label: string; description?: string } }) {
  return (
    <div className="bg-zinc-800 border border-zinc-600 rounded-lg px-4 py-2.5 min-w-[140px] max-w-[200px] shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-zinc-500 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Left} id="target-left" className="!bg-zinc-500 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Right} id="target-right" className="!bg-zinc-500 !w-2 !h-2 !border-0" />
      <div className="text-[12px] font-medium text-zinc-200 text-center leading-tight">{data.label}</div>
      {data.description && (
        <div className="text-[10px] text-zinc-500 text-center mt-1 leading-tight">{data.description}</div>
      )}
      <Handle type="source" position={Position.Bottom} className="!bg-zinc-500 !w-2 !h-2 !border-0" />
      <Handle type="source" position={Position.Right} id="right" className="!bg-zinc-500 !w-2 !h-2 !border-0" />
      <Handle type="source" position={Position.Left} id="left" className="!bg-zinc-500 !w-2 !h-2 !border-0" />
    </div>
  );
}

function DecisionNode({ data }: { data: { label: string } }) {
  return (
    <div className="relative">
      <Handle type="target" position={Position.Top} className="!bg-amber-500 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Left} id="target-left" className="!bg-amber-500 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Right} id="target-right" className="!bg-amber-500 !w-2 !h-2 !border-0" />
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
      <Handle type="source" position={Position.Right} id="right" className="!bg-emerald-500 !w-2 !h-2 !border-0" />
    </div>
  );
}

function EndNode({ data }: { data: { label: string } }) {
  return (
    <div className="bg-red-500/10 border border-red-500/30 rounded-full px-5 py-2 shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-red-400 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Left} id="target-left" className="!bg-red-400 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Right} id="target-right" className="!bg-red-400 !w-2 !h-2 !border-0" />
      <div className="text-[12px] font-semibold text-red-400 text-center">{data.label}</div>
    </div>
  );
}

function SubWorkflowNode({ data }: { data: { label: string; description?: string } }) {
  return (
    <div className="bg-blue-500/10 border-2 border-blue-500/30 border-dashed rounded-lg px-4 py-2.5 min-w-[150px] max-w-[200px] shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-blue-400 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Left} id="target-left" className="!bg-blue-400 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Right} id="target-right" className="!bg-blue-400 !w-2 !h-2 !border-0" />
      <div className="text-[10px] text-blue-400/60 uppercase tracking-wider mb-0.5">sub-workflow</div>
      <div className="text-[12px] font-medium text-blue-300 text-center leading-tight">{data.label}</div>
      {data.description && (
        <div className="text-[10px] text-blue-400/50 text-center mt-1">{data.description}</div>
      )}
      <Handle type="source" position={Position.Bottom} className="!bg-blue-400 !w-2 !h-2 !border-0" />
      <Handle type="source" position={Position.Right} id="right" className="!bg-blue-400 !w-2 !h-2 !border-0" />
      <Handle type="source" position={Position.Left} id="left" className="!bg-blue-400 !w-2 !h-2 !border-0" />
    </div>
  );
}

function ParallelNode({ data }: { data: { label: string; items: string[] } }) {
  return (
    <div className="bg-purple-500/10 border border-purple-500/30 rounded-lg px-4 py-3 min-w-[180px] shadow-lg shadow-black/30">
      <Handle type="target" position={Position.Top} className="!bg-purple-400 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Left} id="target-left" className="!bg-purple-400 !w-2 !h-2 !border-0" />
      <Handle type="target" position={Position.Right} id="target-right" className="!bg-purple-400 !w-2 !h-2 !border-0" />
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
      <Handle type="source" position={Position.Right} id="right" className="!bg-purple-400 !w-2 !h-2 !border-0" />
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

function e(source: string, target: string, label?: string, sourceHandle?: string, targetHandle?: string): Edge {
  return {
    id: `${source}-${target}${sourceHandle || ''}${targetHandle || ''}`,
    source,
    target,
    label,
    sourceHandle: sourceHandle ?? undefined,
    targetHandle: targetHandle ?? undefined,
    type: 'smoothstep',
    ...edgeDefaults,
  };
}

function n(id: string, label: string, x: number, y: number, type = 'process', extra?: Record<string, unknown>): Node {
  return { id, position: { x, y }, data: { label, ...extra }, type };
}

// --- Predefined Workflow Diagrams ---

const WORKFLOW_DIAGRAMS: Record<string, WorkflowDef> = {
  // AdlOrchestratorWorkflow.cs — continuous loop dispatching issue cycles
  'adl-orchestrator': {
    nodes: [
      n('start', 'Load Config', 0, 0, 'start'),
      n('select', 'Select Work Item', 0, 0, 'process', { description: 'Priority-based, multiple sources' }),
      n('selectOutcome', 'Item Found?', 0, 0, 'decision'),
      n('triage', 'Dispatch Triage', 0, 0, 'subworkflow', { description: 'Fire & forget' }),
      n('reportNoIssues', 'Report (No Issues)', 0, 0),
      n('limits', 'Check Limits', 0, 0, 'process', { description: 'Active instances < max' }),
      n('limitsOutcome', 'Within Limits?', 0, 0, 'decision'),
      n('reportLimits', 'Report (Limits)', 0, 0),
      n('dispatch', 'Dispatch Issue Cycle', 0, 0, 'subworkflow', { description: 'Fire & forget' }),
      n('cooldown', 'Cooldown', 0, 0),
      n('restart', 'Dispatch ADL', 0, 0, 'subworkflow', { description: 'New instance' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'select'),
      e('select', 'selectOutcome'),
      e('selectOutcome', 'limits', 'Selected'),
      e('selectOutcome', 'reportNoIssues', 'NothingFound', 'right'),
      e('selectOutcome', 'triage', 'NeedsTriage'),
      e('reportNoIssues', 'cooldown'),
      e('triage', 'cooldown'),
      e('limits', 'limitsOutcome'),
      e('limitsOutcome', 'dispatch', 'Continue'),
      e('limitsOutcome', 'reportLimits', 'Stop', 'right'),
      e('reportLimits', 'cooldown'),
      e('dispatch', 'cooldown'),
      e('cooldown', 'restart'),
      e('restart', 'done'),
    ],
  },
  // SingleIssueCycleWorkflow.cs — Full autonomous dev cycle with revision guards
  // Flow: Init -> Validate [Valid/Invalid] -> GatherContext -> GeneratePlan
  //   -> ReviewPlan -> ReviewOutcome [Approved/NeedsModification/Defer/Split/NeedsHuman]
  //   NeedsModification -> IncrPlanRevision -> PlanMaxRevisions? [True->escalate, False->loop]
  //   Approved -> CreateTasks -> ReviewTasks -> TaskReviewOutcome
  //     [Approved/NeedsChanges/NeedsHuman] + task revision guard
  //   -> CreateBranch -> CreateDraftPR -> CreateTestCases -> TDD Loop
  //   -> DispatchCodeReview + WaitApproval -> DispatchMerge + WaitMerged
  //   -> CloseIssue + DeploymentPipeline -> ReportSuccess -> Finish
  'single-issue-cycle': {
    nodes: [
      n('validate', 'Validate Work Item', 0, 0, 'start'),
      n('invalid', 'Report Error', 0, 0, 'end'),
      n('context', 'Gather Context', 0, 0, 'subworkflow', { description: 'PO LLM + vector DB' }),
      n('plan', 'Generate Plan', 0, 0, 'subworkflow', { description: 'Architect LLM' }),
      n('reviewPlan', 'Review Plan', 0, 0, 'subworkflow', { description: '7-role panel' }),
      n('reviewOutcome', 'Review Outcome', 0, 0, 'decision'),
      n('incrPlanRev', 'Incr Plan Revision', 0, 0),
      n('planMaxRev', 'Plan Max Revisions?', 0, 0, 'decision'),
      n('createTasks', 'Create Tasks', 0, 0, 'subworkflow', { description: 'Senior dev LLM' }),
      n('reviewTasks', 'Review Tasks', 0, 0, 'subworkflow', { description: '4-role panel' }),
      n('taskOutcome', 'Tasks Approved?', 0, 0, 'decision'),
      n('incrTaskRev', 'Incr Task Revision', 0, 0),
      n('taskMaxRev', 'Task Max Revisions?', 0, 0, 'decision'),
      n('branch', 'Create Branch', 0, 0),
      n('draftPR', 'Create Draft PR', 0, 0, 'process', { description: 'Plan .md files' }),
      n('testCases', 'Create Test Cases', 0, 0, 'subworkflow'),
      n('tddLoop', 'TDD Loop', 0, 0, 'subworkflow', { description: 'Per task (red->green->CI->refactor)' }),
      n('codeReview', 'Dispatch Code Review', 0, 0, 'subworkflow', { description: 'Fire & forget' }),
      n('waitApproval', 'Wait for PR Approval', 0, 0, 'process', { description: 'Bookmark' }),
      n('merge', 'Dispatch Merge', 0, 0, 'subworkflow', { description: 'Fire & forget' }),
      n('waitMerged', 'Wait for PR Merged', 0, 0, 'process', { description: 'Bookmark' }),
      n('closeIssue', 'Close Issue', 0, 0),
      n('deploy', 'Deployment Pipeline', 0, 0, 'subworkflow', { description: 'QA -> UAT -> Prod' }),
      n('done', 'Report Success', 0, 0, 'end'),
      n('defer', 'Create Deferred Issues', 0, 0),
      n('split', 'Create Sub-Issues', 0, 0),
      n('needsHuman', 'Report Needs Human', 0, 0, 'end'),
      n('reportDefer', 'Report Deferred', 0, 0, 'end'),
      n('reportSplit', 'Report Split', 0, 0, 'end'),
    ],
    edges: [
      e('validate', 'context', 'Valid'),
      e('validate', 'invalid', 'Invalid', 'right'),
      e('context', 'plan'),
      e('plan', 'reviewPlan'),
      e('reviewPlan', 'reviewOutcome'),
      e('reviewOutcome', 'createTasks', 'Approved'),
      e('reviewOutcome', 'incrPlanRev', 'NeedsModification'),
      e('reviewOutcome', 'defer', 'Defer', 'right'),
      e('reviewOutcome', 'split', 'Split', 'right'),
      e('reviewOutcome', 'needsHuman', 'NeedsHuman', 'right'),
      // Plan revision guard
      e('incrPlanRev', 'planMaxRev'),
      e('planMaxRev', 'plan', 'False'),
      e('planMaxRev', 'needsHuman', 'True', 'right'),
      e('defer', 'reportDefer'),
      e('split', 'reportSplit'),
      e('createTasks', 'reviewTasks'),
      e('reviewTasks', 'taskOutcome'),
      e('taskOutcome', 'branch', 'Approved'),
      e('taskOutcome', 'incrTaskRev', 'NeedsChanges'),
      e('taskOutcome', 'needsHuman', 'NeedsHuman', 'right'),
      // Task revision guard
      e('incrTaskRev', 'taskMaxRev'),
      e('taskMaxRev', 'createTasks', 'False'),
      e('taskMaxRev', 'needsHuman', 'True', 'right'),
      e('branch', 'draftPR'),
      e('draftPR', 'testCases'),
      e('testCases', 'tddLoop'),
      e('tddLoop', 'codeReview'),
      e('tddLoop', 'waitApproval'),
      e('waitApproval', 'merge'),
      e('waitApproval', 'waitMerged'),
      e('waitMerged', 'closeIssue'),
      e('waitMerged', 'deploy'),
      e('deploy', 'done'),
    ],
  },
  // TddWorkflow.cs — Red-Green-Refactor TDD cycle
  // RED: WriteTests -> CheckTestsFail -> (TestsFail->GREEN, TestsPass->MaxRewrites?->rewrite/GREEN)
  // GREEN: WriteImpl -> GreenTestsPass? -> (True->REFACTOR, False->MaxDebug?->retry/Failed)
  // REFACTOR: AnalyzeCode -> RefactoringNeeded? -> ApplyRefactoring -> verify -> Commit
  'tdd-cycle': {
    nodes: [
      n('start', 'Start', 250, 0, 'start'),
      n('write-tests', 'Write Tests', 250, 90, 'process', { description: 'RED phase' }),
      n('check-fail', 'Check Tests Fail', 250, 190, 'decision'),
      n('max-rewrites', 'Max Rewrites?', 500, 190, 'decision'),
      n('write-impl', 'Write Implementation', 250, 300, 'process', { description: 'GREEN phase' }),
      n('green-pass', 'Green Tests Pass?', 250, 400, 'decision'),
      n('max-debug', 'Max Debug?', 500, 400, 'decision'),
      n('analyze', 'Analyze Code', 250, 510, 'process', { description: 'REFACTOR phase' }),
      n('refactor-needed', 'Refactoring Needed?', 250, 610, 'decision'),
      n('apply-refactor', 'Apply Refactoring', 250, 720),
      n('refactor-pass', 'Refactor Tests Pass?', 250, 820, 'decision'),
      n('revert', 'Revert Refactoring', 500, 820),
      n('commit', 'Commit Changes', 250, 930),
      n('index', 'Update Code Index', 250, 1020),
      n('done', 'Finish Success', 250, 1110, 'end'),
      n('failed', 'Finish Failed', 500, 510, 'end'),
    ],
    edges: [
      e('start', 'write-tests'),
      e('write-tests', 'check-fail'),
      e('check-fail', 'write-impl', 'TestsFail'),
      e('check-fail', 'max-rewrites', 'TestsPass', 'right'),
      e('max-rewrites', 'write-impl', 'True'),
      e('max-rewrites', 'write-tests', 'False'),
      e('write-impl', 'green-pass'),
      e('green-pass', 'analyze', 'True'),
      e('green-pass', 'max-debug', 'False', 'right'),
      e('max-debug', 'failed', 'True', 'right'),
      e('max-debug', 'write-impl', 'False'),
      e('analyze', 'refactor-needed'),
      e('refactor-needed', 'apply-refactor', 'True'),
      e('refactor-needed', 'commit', 'False'),
      e('apply-refactor', 'refactor-pass'),
      e('refactor-pass', 'commit', 'True'),
      e('refactor-pass', 'revert', 'False', 'right'),
      e('revert', 'commit'),
      e('commit', 'index'),
      e('index', 'done'),
    ],
  },
  // LlmCallWorkflow.cs — Universal LLM call with provider chain
  // Flow: InitInputs -> SetupBudget -> ResolveAgentConfig -> ResolveChain
  //   -> ForEachProvider (CircuitBreaker + Budget + RetryLoop(CallLLM))
  //   -> Call Succeeded? -> SetOutputs | BuildFailureOutput -> SetOutputs
  'llm-call': {
    nodes: [
      n('start', 'Initialize Inputs', 0, 0, 'start'),
      n('resolve', 'Resolve Prompt', 0, 0, 'process', { description: 'Registry: role + action → template' }),
      n('budget', 'Setup Budget', 0, 0),
      n('agent', 'Resolve Agent Config', 0, 0),
      n('chain', 'Resolve Provider Chain', 0, 0),
      n('foreach', 'For Each Provider', 0, 0, 'subworkflow', { description: 'Iterate provider chain' }),
      n('skipCheck', 'Already Succeeded?', 0, 0, 'decision'),
      n('circuitBreaker', 'Circuit Breaker Open?', 0, 0, 'decision'),
      n('budgetCheck', 'Budget Exceeded?', 0, 0, 'decision'),
      n('callLlm', 'Call LLM', 0, 0, 'process', { description: 'With rendered prompt' }),
      n('llmOk', 'LLM Succeeded?', 0, 0, 'decision'),
      n('transient', 'Transient Error?', 0, 0, 'decision'),
      n('retry', 'Retry with Backoff', 0, 0),
      n('nextProvider', 'Next Provider', 0, 0),
      n('succeeded', 'Call Succeeded?', 0, 0, 'decision'),
      n('outputs', 'Set Outputs', 0, 0),
      n('done', 'Done', 0, 0, 'end'),
      n('failure', 'Build Failure Output', 0, 0),
    ],
    edges: [
      e('start', 'resolve'),
      e('resolve', 'budget'),
      e('budget', 'agent'),
      e('agent', 'chain'),
      e('chain', 'foreach'),
      e('foreach', 'skipCheck'),
      e('skipCheck', 'circuitBreaker', 'No'),
      e('skipCheck', 'succeeded', 'Yes', 'right'),
      e('circuitBreaker', 'budgetCheck', 'Closed'),
      e('circuitBreaker', 'nextProvider', 'Open', 'right'),
      e('budgetCheck', 'callLlm', 'OK'),
      e('budgetCheck', 'nextProvider', 'Exceeded', 'right'),
      e('callLlm', 'llmOk'),
      e('llmOk', 'succeeded', 'Yes'),
      e('llmOk', 'transient', 'No', 'right'),
      e('transient', 'retry', 'Yes'),
      e('transient', 'nextProvider', 'No', 'right'),
      e('retry', 'callLlm'),
      e('nextProvider', 'foreach'),
      e('succeeded', 'outputs', 'True'),
      e('succeeded', 'failure', 'False', 'right'),
      e('failure', 'outputs'),
      e('outputs', 'done'),
    ],
  },
  // MentorshipWorkflow.cs — Full session lifecycle with 28 states
  // Happy: Init -> ContextGathering -> Validate -> Assess -> Plan -> ReviewPlan
  //   -> StartImpl -> TDD -> Monitor -> QualityGate -> Testing -> CodeReview
  //   -> MonitorReview -> Merge -> Report -> Profile -> Completed
  // Branches: Blocker escalation, Bug fast path, Assessment loops, Quality retry
  'mentorship': {
    nodes: [
      n('init', 'Initialize Story Processing', 250, 0, 'start'),
      n('ctxGather', 'Context Gathering', 250, 90, 'subworkflow'),
      n('validate', 'Validate Story', 250, 180),
      n('assess', 'Assess Junior Capability', 250, 290),
      n('plan', 'Plan Decomposition', 250, 400),
      n('reviewPlan', 'Review Plan', 250, 490),
      n('impl', 'Start Implementation', 250, 590),
      n('tdd', 'TDD Cycle', 250, 680, 'subworkflow'),
      n('monitor', 'Monitor Progress', 250, 770),
      n('quality', 'Quality Gate Check', 250, 880),
      n('testing', 'Testing Pipeline', 250, 970, 'subworkflow'),
      n('prepReview', 'Prepare Code Review', 250, 1060),
      n('codeReview', 'Code Review', 250, 1150, 'subworkflow'),
      n('monReview', 'Monitor Review Status', 250, 1240),
      n('merge', 'Merge and Complete', 250, 1350),
      n('report', 'Generate Session Report', 250, 1440),
      n('profile', 'Update Skill Profile', 250, 1530),
      n('done', 'Session Completed', 250, 1620, 'end'),
      n('diagnose', 'Diagnose Blocker', 520, 770, 'subworkflow'),
      n('hint', 'Provide Hint (Socratic)', 520, 880),
      n('guidance', 'Provide Direct Guidance', 520, 990),
      n('assistance', 'Provide Code Assistance', 520, 1090),
      n('escalate', 'Escalate to Senior Developer', 520, 1200, 'end'),
      n('autofix', 'Auto-Fix Quality Issues', 520, 400),
      n('guideFixes', 'Guide Review Fixes', 520, 1350),
      n('debug', 'Debugging', 520, 180, 'subworkflow', { description: 'Bug fast path' }),
      n('failed', 'Failed', 750, 290, 'end'),
    ],
    edges: [
      e('init', 'ctxGather'),
      e('ctxGather', 'validate'),
      e('validate', 'assess', 'Valid'),
      e('validate', 'debug', 'BugIssue', 'right'),
      e('validate', 'failed', 'Invalid', 'right'),
      e('debug', 'quality'),
      e('assess', 'plan', 'Correct'),
      e('assess', 'diagnose', 'Timeout', 'right'),
      e('plan', 'reviewPlan', 'Planned'),
      e('reviewPlan', 'impl', 'Approved'),
      e('impl', 'tdd', 'Started'),
      e('tdd', 'monitor'),
      e('monitor', 'quality', 'Complete'),
      e('monitor', 'diagnose', 'Stalled', 'right'),
      e('monitor', 'guidance', 'Slowing', 'right'),
      e('quality', 'testing', 'Passed'),
      e('quality', 'autofix', 'Failed', 'right'),
      e('autofix', 'quality', 'Fixed'),
      e('testing', 'prepReview'),
      e('prepReview', 'codeReview', 'Prepared'),
      e('codeReview', 'monReview'),
      e('monReview', 'merge', 'Approved'),
      e('monReview', 'guideFixes', 'ChangesRequested', 'right'),
      e('guideFixes', 'monReview'),
      e('merge', 'report', 'Merged'),
      e('report', 'profile', 'Generated'),
      e('profile', 'done', 'Updated'),
      e('diagnose', 'hint', 'Hint'),
      e('diagnose', 'guidance', 'Guidance'),
      e('diagnose', 'assistance', 'Assistance'),
      e('diagnose', 'escalate', 'Escalate'),
      e('hint', 'monitor', 'Done'),
      e('hint', 'guidance', 'Error'),
      e('guidance', 'monitor', 'Done'),
      e('guidance', 'assistance', 'Error'),
      e('assistance', 'impl', 'Done'),
      e('assistance', 'escalate', 'Error'),
    ],
  },
  // CodeReviewWorkflow.cs — Full PR lifecycle with bookmark-based waiting
  // Flow: CreatePR -> StorePRResult -> PRCreated?
  //   True -> RequestReview -> MonitorReview
  //     Approved -> MergeAndComplete -> Success
  //     Commented -> MonitorReview (self-loop)
  //     ChangesRequested -> StoreComments -> IncrementIteration -> DeliverGuidance
  //       -> WaitForFixes [FixesReceived -> ReRequest -> MaxIterations?]
  //     TimedOut -> EscalateTimeout
  //   False -> Failure
  //   Escalation: Resolved -> Merge, Rejected -> Fail
  'code-review': {
    nodes: [
      n('start', 'Create Pull Request', 0, 0, 'start'),
      n('storePR', 'Store PR Result', 0, 0),
      n('prCheck', 'PR Created?', 0, 0, 'decision'),
      n('request', 'Request Code Review', 0, 0),
      n('monitor', 'Monitor Review Status', 0, 0, 'process', { description: 'Bookmark (24h timeout)' }),
      n('merge', 'Merge and Complete Review', 0, 0),
      n('success', 'Success', 0, 0, 'end'),
      n('storeComments', 'Store Review Comments', 0, 0),
      n('incrIter', 'Increment Iteration', 0, 0),
      n('guidance', 'Deliver Fix Guidance', 0, 0),
      n('waitFixes', 'Wait for Fix Submission', 0, 0, 'process', { description: 'Bookmark (24h)' }),
      n('rerequest', 'Re-Request Code Review', 0, 0),
      n('maxIter', 'Max Iterations Reached?', 0, 0, 'decision'),
      n('escalateMax', 'Escalate: Max Iterations', 0, 0, 'process', { description: 'Bookmark' }),
      n('escalateTimeout', 'Escalate: Review Timeout', 0, 0, 'process', { description: 'Bookmark' }),
      n('failEnd', 'Failure', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'storePR'),
      e('storePR', 'prCheck'),
      e('prCheck', 'request', 'True'),
      e('prCheck', 'failEnd', 'False', 'left'),
      e('request', 'monitor'),
      e('monitor', 'merge', 'Approved'),
      e('monitor', 'monitor', 'Commented', 'right', 'target-right'),
      e('monitor', 'storeComments', 'ChangesRequested'),
      e('monitor', 'escalateTimeout', 'TimedOut', 'right'),
      e('storeComments', 'incrIter'),
      e('incrIter', 'guidance'),
      e('guidance', 'waitFixes'),
      e('waitFixes', 'rerequest', 'FixesReceived'),
      e('waitFixes', 'escalateTimeout', 'TimedOut', 'right'),
      e('rerequest', 'maxIter'),
      e('maxIter', 'monitor', 'False'),
      e('maxIter', 'escalateMax', 'True', 'right'),
      e('merge', 'success'),
      e('escalateMax', 'merge', 'Resolved'),
      e('escalateMax', 'failEnd', 'Rejected', 'right'),
      e('escalateTimeout', 'merge', 'Resolved'),
      e('escalateTimeout', 'failEnd', 'Rejected'),
    ],
  },
  // TestingWorkflow.cs — Testing pipeline with CI, quality checks, auto-fix
  // Flow: TriggerCI -> WaitForCI -> StoreCIResults -> EvaluateResults
  //   AllPass -> Quality Checks -> Report -> Pass
  //   MinorIssues -> Quality Checks (same path as AllPass)
  //   MajorIssues -> FixAttemptsRemaining? -> CommitFix -> UpdateCodeIndex
  //     -> IncrementAttempt -> ReTriggerCI -> WaitRetry -> EvalRetry -> route
  //   Critical -> Quality Checks (Critical) -> Report (Critical) -> Fail
  'testing': {
    nodes: [
      n('start', 'Trigger CI Pipeline', 0, 0, 'start'),
      n('wait', 'Wait for CI Results', 0, 0, 'process', { description: 'Bookmark (30min)' }),
      n('evaluate', 'Evaluate CI Results', 0, 0),
      n('checks', 'Quality Checks', 0, 0, 'parallel', { items: ['Coverage', 'Linting', 'Security'] }),
      n('report', 'Generate Quality Report', 0, 0),
      n('pass', 'Complete: Tests Passed', 0, 0, 'end'),
      n('guard', 'Fix Attempts Remaining?', 0, 0, 'decision'),
      n('fix', 'Commit Auto-Fix', 0, 0),
      n('index', 'Update Code Index', 0, 0),
      n('incrAttempt', 'Increment Fix Attempt', 0, 0),
      n('retrigger', 'Re-Trigger CI After Fix', 0, 0),
      n('waitRetry', 'Wait for CI Results (Retry)', 0, 0, 'process', { description: 'Bookmark' }),
      n('evalRetry', 'Evaluate Retry Results', 0, 0),
      n('fail', 'Complete: Tests Failed', 0, 0, 'end'),
      n('critChecks', 'Quality Checks (Critical)', 0, 0, 'parallel', { items: ['Coverage', 'Linting', 'Security'] }),
      n('critReport', 'Generate Report (Critical)', 0, 0),
    ],
    edges: [
      e('start', 'wait'),
      e('wait', 'evaluate'),
      e('evaluate', 'checks', 'AllPass'),
      e('evaluate', 'checks', 'MinorIssues'),
      e('evaluate', 'guard', 'MajorIssues', 'right'),
      e('evaluate', 'critChecks', 'Critical', 'left'),
      e('checks', 'report'),
      e('report', 'pass'),
      e('guard', 'fix', 'True'),
      e('guard', 'fail', 'False'),
      e('fix', 'index'),
      e('index', 'incrAttempt'),
      e('incrAttempt', 'retrigger'),
      e('retrigger', 'waitRetry'),
      e('waitRetry', 'evalRetry'),
      e('evalRetry', 'checks', 'AllPass'),
      e('evalRetry', 'checks', 'MinorIssues'),
      e('evalRetry', 'guard', 'MajorIssues'),
      e('evalRetry', 'fail', 'Critical'),
      e('critChecks', 'critReport'),
      e('critReport', 'fail'),
    ],
  },
  // DebuggingWorkflow.cs — Systematic AI debugging with 3 entry modes
  // Flow: Init chain -> Classify [TddFailure/RuntimeError/BugInvestigation]
  //   -> TDD/Runtime/Bug Emphasis -> FlowFork (5 parallel collectors)
  //   -> FlowJoin -> Serialize -> AIDiagnosis -> SelectHypothesis -> Has Hypothesis?
  //   True -> IsBugMode? -> (optional WriteRegressionTest) -> ApplyFix (LLM Call)
  //     -> RunTests (Testing Pipeline) -> Tests Pass?
  //     True -> RecordResolution -> UpdateCodeIndex -> Resolved
  //     False -> RefineHypothesis -> loop | False -> CompileDebugReport -> Escalated
  'debugging': {
    nodes: [
      n('start', 'Initialize', 0, 0, 'start'),
      n('classify', 'Classify Debug Context', 0, 0, 'decision'),
      n('tddEmph', 'TDD Emphasis', 0, 0, 'process', { description: 'Test output focus' }),
      n('runtimeEmph', 'Runtime Emphasis', 0, 0, 'process', { description: 'Stack trace focus' }),
      n('bugEmph', 'Bug Emphasis', 0, 0, 'process', { description: 'Repro steps focus' }),
      n('fork', 'Context Fork', 0, 0, 'parallel', { items: ['Errors', 'Code', 'Git', 'Tests', 'Repro'] }),
      n('join', 'Context Join', 0, 0),
      n('diagnose', 'AI Diagnosis', 0, 0),
      n('select', 'Select Hypothesis', 0, 0),
      n('hasHyp', 'Has Hypothesis?', 0, 0, 'decision'),
      n('bugMode', 'Is Bug Mode?', 0, 0, 'decision'),
      n('regTest', 'Write Regression Test', 0, 0),
      n('fix', 'Apply Fix', 0, 0, 'subworkflow', { description: 'via LLM Call' }),
      n('test', 'Run Tests', 0, 0, 'subworkflow', { description: 'via Testing Pipeline' }),
      n('pass', 'Tests Pass?', 0, 0, 'decision'),
      n('record', 'Record Resolution', 0, 0),
      n('index', 'Update Code Index', 0, 0),
      n('done', 'Complete: Debugging Done', 0, 0, 'end'),
      n('refine', 'Refine Hypothesis', 0, 0),
      n('report', 'Compile Debug Report', 0, 0),
      n('escalated', 'Escalated', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'classify'),
      e('classify', 'tddEmph', 'TddFailure'),
      e('classify', 'runtimeEmph', 'RuntimeError'),
      e('classify', 'bugEmph', 'BugInvestigation', 'right'),
      e('tddEmph', 'fork'),
      e('runtimeEmph', 'fork'),
      e('bugEmph', 'fork'),
      e('fork', 'join'),
      e('join', 'diagnose'),
      e('diagnose', 'select'),
      e('select', 'hasHyp'),
      e('hasHyp', 'bugMode', 'True'),
      e('hasHyp', 'report', 'False', 'right'),
      e('bugMode', 'regTest', 'True', 'right'),
      e('bugMode', 'fix', 'False'),
      e('regTest', 'fix'),
      e('fix', 'test'),
      e('test', 'pass'),
      e('pass', 'record', 'True'),
      e('pass', 'refine', 'False', 'right'),
      e('refine', 'select'),
      e('record', 'index'),
      e('index', 'done'),
      e('report', 'escalated'),
    ],
  },
  // BlockerDiagnosisWorkflow.cs — Progressive resolution (hint->guidance->assistance->escalation)
  // Flow: CaptureInputs -> Collect Signals (parallel: Git,CI,Inactivity,Comms)
  //   -> AggregateSignals -> AI Diagnosis (LLM Call) -> ClassifyBlocker
  //   -> DetermineStartLevel -> Hint -> Guidance -> Assistance -> Escalation -> Output
  // Each level checks if resolved; skips if already resolved
  'blocker-diagnosis': {
    nodes: [
      n('start', 'Capture Inputs', 250, 0, 'start'),
      n('signals', 'Collect Signals', 250, 100, 'parallel', { items: ['Git', 'CI', 'Inactivity', 'Comms'] }),
      n('aggregate', 'Aggregate Signals', 250, 240),
      n('ai', 'AI Diagnosis', 250, 340, 'subworkflow', { description: 'via LLM Call' }),
      n('classify', 'Classify Blocker', 250, 440, 'process', { description: '8 categories' }),
      n('startLevel', 'Determine Start Level', 250, 540, 'process', { description: 'Skill-adapted' }),
      n('hint', 'Level 1: Hint', 250, 640, 'process', { description: 'Socratic (15-30min wait)' }),
      n('guidance', 'Level 2: Guidance', 250, 740, 'process', { description: 'Direct guidance (30min wait)' }),
      n('assist', 'Level 3: Assistance', 250, 840, 'process', { description: 'Code examples (45min wait)' }),
      n('escalate', 'Level 4: Escalation', 250, 940, 'process', { description: 'Senior developer' }),
      n('output', 'Output: Blocker Resolution', 250, 1040, 'end'),
    ],
    edges: [
      e('start', 'signals'),
      e('signals', 'aggregate'),
      e('aggregate', 'ai'),
      e('ai', 'classify'),
      e('classify', 'startLevel'),
      e('startLevel', 'hint'),
      e('hint', 'guidance'),
      e('guidance', 'assist'),
      e('assist', 'escalate'),
      e('escalate', 'output'),
    ],
  },
  // ContextGatheringWorkflow.cs — Two-phase parallel context gathering
  // Flow: InitInputs -> Phase 1 (Story,Commits,Tests,History) -> Story Metadata OK?
  //   True -> Phase 2 (FileContents,SimilarPatterns) -> AssembleContext -> ApplyBudget -> SetOutputs
  //   False -> Fault (No Metadata)
  // ContextGatheringWorkflow.cs — Sequential role scans with per-role vector DB storage
  'context-gathering': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start', { description: 'Repo, issue, workItemType' }),
      n('devScan', 'Dev Scan', 0, 0, 'subworkflow', { description: 'LLM Call: developer' }),
      n('storeDev', 'Store Dev', 0, 0, 'process', { description: 'Vector DB' }),
      n('qaScan', 'QA Scan', 0, 0, 'subworkflow', { description: 'LLM Call: tester' }),
      n('storeQA', 'Store QA', 0, 0, 'process', { description: 'Vector DB' }),
      n('secScan', 'Security Scan', 0, 0, 'subworkflow', { description: 'LLM Call: security' }),
      n('storeSec', 'Store Security', 0, 0, 'process', { description: 'Vector DB' }),
      n('devopsScan', 'DevOps Scan', 0, 0, 'subworkflow', { description: 'LLM Call: devops' }),
      n('storeDevOps', 'Store DevOps', 0, 0, 'process', { description: 'Vector DB' }),
      n('archScan', 'Architect Scan', 0, 0, 'subworkflow', { description: 'LLM Call: architect' }),
      n('storeArch', 'Store Architect', 0, 0, 'process', { description: 'Vector DB' }),
      n('poReview', 'PO Review', 0, 0, 'subworkflow', { description: 'LLM Call: summarize' }),
      n('outputs', 'Set Outputs', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'devScan'),
      e('devScan', 'storeDev'),
      e('storeDev', 'qaScan'),
      e('qaScan', 'storeQA'),
      e('storeQA', 'secScan'),
      e('secScan', 'storeSec'),
      e('storeSec', 'devopsScan'),
      e('devopsScan', 'storeDevOps'),
      e('storeDevOps', 'archScan'),
      e('archScan', 'storeArch'),
      e('storeArch', 'poReview'),
      e('poReview', 'outputs'),
      e('outputs', 'done'),
    ],
  },
  // IssueTriageWorkflow.cs — Fetch items, dispatch singleton triage cycles
  'triage': {
    nodes: [
      n('fetch', 'Fetch Untriaged Items', 0, 0, 'start', { description: 'Issues + Dependabot + CodeQL' }),
      n('hasItems', 'Has Items?', 0, 0, 'decision'),
      n('extract', 'Extract Current Item', 0, 0),
      n('dispatch', 'Dispatch Triage Cycle', 0, 0, 'subworkflow', { description: 'Fire & forget (singleton)' }),
      n('next', 'Next Item', 0, 0),
      n('more', 'More Items?', 0, 0, 'decision'),
      n('report', 'Report Triage Complete', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('fetch', 'hasItems'),
      e('hasItems', 'extract', 'True'),
      e('hasItems', 'report', 'False', 'right'),
      e('extract', 'dispatch'),
      e('dispatch', 'next'),
      e('next', 'more'),
      e('more', 'extract', 'True'),
      e('more', 'report', 'False', 'right'),
      e('report', 'done'),
    ],
  },
  // TriageItemCycleWorkflow.cs — Singleton: context → panel → PO → labels
  'triage-item-cycle': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('context', 'Gather Triage Context', 0, 0, 'subworkflow', { description: 'Code usage, deps, CVE' }),
      n('panel', 'Panel Review', 0, 0, 'subworkflow', { description: 'Security / Dev / DevOps / QA' }),
      n('po', 'PO Decision', 0, 0, 'subworkflow', { description: 'Priority, type, labels' }),
      n('apply', 'Apply Labels & Comment', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'context'),
      e('context', 'panel'),
      e('panel', 'po'),
      e('po', 'apply'),
      e('apply', 'done'),
    ],
  },
  // PlanGenerationWorkflow.cs — Architect LLM generates plan, validates, retries
  'plan-generation': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('generate', 'Generate Plan', 0, 0, 'subworkflow', { description: 'LLM Call (architect)' }),
      n('validate', 'Extract & Validate', 0, 0),
      n('valid', 'Plan Valid?', 0, 0, 'decision'),
      n('output', 'Output Plan', 0, 0),
      n('incrRetry', 'Increment Retry', 0, 0),
      n('canRetry', 'Can Retry?', 0, 0, 'decision'),
      n('error', 'Error Outputs', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'generate'),
      e('generate', 'validate'),
      e('validate', 'valid'),
      e('valid', 'output', 'Yes'),
      e('valid', 'incrRetry', 'No', 'right'),
      e('output', 'done'),
      e('incrRetry', 'canRetry'),
      e('canRetry', 'generate', 'Yes'),
      e('canRetry', 'error', 'No', 'right'),
      e('error', 'done'),
    ],
  },
  // PlanReviewWorkflow.cs — 7-role panel with discussion rounds
  'plan-review': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('arch', 'Architect Review', 0, 0, 'subworkflow'),
      n('dev', 'Developer Review', 0, 0, 'subworkflow'),
      n('tester', 'Tester Review', 0, 0, 'subworkflow'),
      n('sec', 'Security Review', 0, 0, 'subworkflow'),
      n('devops', 'DevOps Review', 0, 0, 'subworkflow'),
      n('po', 'PO Review', 0, 0, 'subworkflow'),
      n('srdev', 'Senior Dev Review', 0, 0, 'subworkflow'),
      n('aggregate', 'Aggregate Verdicts', 0, 0),
      n('allApproved', 'All Approved?', 0, 0, 'decision'),
      n('approved', 'Set Approved', 0, 0),
      n('discussion', 'Discussion Round', 0, 0, 'subworkflow', { description: 'PO resolves concerns' }),
      n('needsReReview', 'Needs Re-review?', 0, 0, 'decision'),
      n('incrRound', 'Increment Round', 0, 0),
      n('canContinue', 'Round <= 3?', 0, 0, 'decision'),
      n('forceHuman', 'Force Needs Human', 0, 0),
      n('outputs', 'Set Outputs', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'arch'),
      e('arch', 'dev'), e('dev', 'tester'), e('tester', 'sec'),
      e('sec', 'devops'), e('devops', 'po'), e('po', 'srdev'),
      e('srdev', 'aggregate'),
      e('aggregate', 'allApproved'),
      e('allApproved', 'approved', 'Yes'),
      e('allApproved', 'discussion', 'No', 'right'),
      e('approved', 'outputs'),
      e('discussion', 'needsReReview'),
      e('needsReReview', 'incrRound', 'Yes'),
      e('needsReReview', 'outputs', 'No', 'right'),
      e('incrRound', 'canContinue'),
      e('canContinue', 'arch', 'Yes'),
      e('canContinue', 'forceHuman', 'No', 'right'),
      e('forceHuman', 'outputs'),
      e('outputs', 'done'),
    ],
  },
  // TddWithDebugRetryWorkflow.cs — TDD with up to 3 debug retries
  'tdd-with-debug-retry': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('tdd', 'TDD Cycle', 0, 0, 'subworkflow'),
      n('passed', 'TDD Passed?', 0, 0, 'decision'),
      n('guard', 'TDD Debug < 3?', 0, 0, 'decision'),
      n('incr', 'Increment TDD Debug', 0, 0),
      n('debug', 'Debug TDD Failure', 0, 0, 'subworkflow'),
      n('pass', 'Finish (Pass)', 0, 0, 'end'),
      n('fail', 'Finish (Fail)', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'tdd'),
      e('tdd', 'passed'),
      e('passed', 'pass', 'True'),
      e('passed', 'guard', 'False', 'right'),
      e('guard', 'incr', 'True'),
      e('guard', 'fail', 'False', 'right'),
      e('incr', 'debug'),
      e('debug', 'tdd'),
    ],
  },
  // CiWithDebugRetryWorkflow.cs — CI with up to 3 debug retries
  'ci-with-debug-retry': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('ci', 'Testing Pipeline', 0, 0, 'subworkflow'),
      n('passed', 'Tests Passed?', 0, 0, 'decision'),
      n('guard', 'CI Retries < 3?', 0, 0, 'decision'),
      n('incr', 'Increment CI Retry', 0, 0),
      n('debug', 'Debug CI Failure', 0, 0, 'subworkflow'),
      n('pass', 'Finish (Pass)', 0, 0, 'end'),
      n('fail', 'Finish (Fail)', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'ci'),
      e('ci', 'passed'),
      e('passed', 'pass', 'True'),
      e('passed', 'guard', 'False', 'right'),
      e('guard', 'incr', 'True'),
      e('guard', 'fail', 'False', 'right'),
      e('incr', 'debug'),
      e('debug', 'ci'),
    ],
  },
  // AssessmentWorkflow.cs — Junior developer skill assessment
  // Flow: ReadInputs -> GatherContext -> StoreContext -> GenerateQuestions -> StoreQuestions
  //   -> DeliverQuestions -> WaitForResponse
  //   [Responded] -> StoreResponse -> Analyze -> StoreAnalysis -> Classify -> StoreClassification
  //     -> UpdateProfile -> SetOutput -> ExposeOutput -> Done
  //   [Timeout] -> SetTimeoutResult -> UpdateProfile(Timeout) -> SetOutputTimeout -> ExposeOutputTimeout -> Done
  'assessment': {
    nodes: [
      n('init', 'Read Inputs', 0, 0, 'start'),
      n('context', 'Gather Context', 0, 0, 'subworkflow', { description: 'Dispatch context-gathering' }),
      n('storeCtx', 'Store Context Result', 0, 0),
      n('generate', 'Generate Questions', 0, 0, 'process', { description: 'AI-generated questions' }),
      n('storeQ', 'Store Questions', 0, 0),
      n('deliver', 'Deliver Questions', 0, 0),
      n('wait', 'Wait for Response', 0, 0, 'process', { description: 'Bookmark (with timeout)' }),
      n('storeResp', 'Store Response', 0, 0),
      n('analyze', 'Analyze Response', 0, 0, 'process', { description: 'AI analysis' }),
      n('storeAnalysis', 'Store Analysis', 0, 0),
      n('classify', 'Classify Result', 0, 0),
      n('storeClass', 'Store Classification', 0, 0),
      n('profile', 'Update Skill Profile', 0, 0),
      n('setOutput', 'Set Output Result', 0, 0),
      n('expose', 'Expose Outputs', 0, 0),
      n('timeout', 'Set Timeout Result', 0, 0),
      n('profileTimeout', 'Update Skill Profile (Timeout)', 0, 0),
      n('setOutputTimeout', 'Set Output Timeout', 0, 0),
      n('exposeTimeout', 'Expose Outputs (Timeout)', 0, 0),
    ],
    edges: [
      e('init', 'context'),
      e('context', 'storeCtx'),
      e('storeCtx', 'generate'),
      e('generate', 'storeQ'),
      e('storeQ', 'deliver'),
      e('deliver', 'wait'),
      // Response path
      e('wait', 'storeResp', 'Responded'),
      e('storeResp', 'analyze'),
      e('analyze', 'storeAnalysis'),
      e('storeAnalysis', 'classify'),
      e('classify', 'storeClass'),
      e('storeClass', 'profile'),
      e('profile', 'setOutput'),
      e('setOutput', 'expose'),
      // Timeout path
      e('wait', 'timeout', 'Timeout', 'right'),
      e('timeout', 'profileTimeout'),
      e('profileTimeout', 'setOutputTimeout'),
      e('setOutputTimeout', 'exposeTimeout'),
    ],
  },
  // ReviewFixWorkflow.cs — Analyze review comments and apply AI-generated fixes
  // Flow: AnalyzeReview -> HasActionable?
  //   True -> GenerateFixes (LLM) -> ApplyFixes -> UpdateCodeIndex -> OutputSuccess
  //   False -> OutputSuccess
  //   -> OutputHasComments -> OutputFixesApplied
  'review-fix': {
    nodes: [
      n('analyze', 'Analyze Review', 0, 0, 'start', { description: 'Parse PR comments' }),
      n('hasActionable', 'Has Actionable?', 0, 0, 'decision'),
      n('generate', 'Generate Fixes', 0, 0, 'subworkflow', { description: 'LLM Call' }),
      n('apply', 'Apply Fixes', 0, 0),
      n('index', 'Update Code Index', 0, 0),
      n('outputSuccess', 'Output Success', 0, 0),
      n('outputComments', 'Output Has Comments', 0, 0),
      n('outputFixes', 'Output Fixes Applied', 0, 0, 'end'),
    ],
    edges: [
      e('analyze', 'hasActionable'),
      e('hasActionable', 'generate', 'True'),
      e('hasActionable', 'outputSuccess', 'False', 'right'),
      e('generate', 'apply'),
      e('apply', 'index'),
      e('index', 'outputSuccess'),
      e('outputSuccess', 'outputComments'),
      e('outputComments', 'outputFixes'),
    ],
  },
  // BranchCreationWorkflow.cs — Create feature branch
  'branch-creation': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('create', 'Create Branch', 0, 0, 'process', { description: 'feature/<issue>-<slug>' }),
      n('output', 'Set Outputs', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'create'),
      e('create', 'output'),
      e('output', 'done'),
    ],
  },
  // PullRequestWorkflow.cs — Create draft PR
  'pull-request': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('create', 'Create Draft PR', 0, 0, 'process', { description: 'Plan .md files attached' }),
      n('output', 'Set Outputs', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'create'),
      e('create', 'output'),
      e('output', 'done'),
    ],
  },
  // MergeApprovalWorkflow.cs — Wait for PR approval
  'merge-approval': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('wait', 'Wait for Approval', 0, 0, 'process', { description: 'Bookmark' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'wait'),
      e('wait', 'done'),
    ],
  },
  // MergeWorkflow.cs — Merge PR
  'merge': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('merge', 'Merge PR', 0, 0),
      n('cleanup', 'Delete Branch', 0, 0),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'merge'),
      e('merge', 'cleanup'),
      e('cleanup', 'done'),
    ],
  },
  // UpdateIssueStatusWorkflow.cs — Fire-and-forget issue notification (single activity)
  // C# uses builder.Root = updateIssue (no flowchart, just one activity)
  'update-issue-status': {
    nodes: [
      n('start', 'Start', 0, 0, 'start'),
      n('update', 'Update Issue Status', 0, 0, 'process', { description: 'Comment + labels (with retries)' }),
      n('done', 'Done', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'update'),
      e('update', 'done'),
    ],
  },
  // DeploymentPipelineWorkflow.cs — Stub
  'deployment-pipeline': {
    nodes: [
      n('init', 'Initialize', 0, 0, 'start'),
      n('qa', 'QA Stage', 0, 0, 'process', { description: 'TODO' }),
      n('uat', 'UAT Stage', 0, 0, 'process', { description: 'TODO' }),
      n('prod', 'Production Stage', 0, 0, 'process', { description: 'TODO' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('init', 'qa'),
      e('qa', 'uat'),
      e('uat', 'prod'),
      e('prod', 'done'),
    ],
  },
  // TaskCreationWorkflow.cs — Stub (single Finish activity)
  'task-creation': {
    nodes: [
      n('start', 'Start', 0, 0, 'start', { description: 'Stub -- TODO' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'done'),
    ],
  },
  // TaskReviewWorkflow.cs — Stub (single Finish activity)
  'task-review': {
    nodes: [
      n('start', 'Start', 0, 0, 'start', { description: 'Stub -- TODO' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'done'),
    ],
  },
  // TestCaseCreationWorkflow.cs — Stub (single Finish activity)
  'test-case-creation': {
    nodes: [
      n('start', 'Start', 0, 0, 'start', { description: 'Stub -- TODO' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'done'),
    ],
  },
  // TriageContextGatheringWorkflow.cs — Stub (SetOutput + Finish sequence)
  'triage-context-gathering': {
    nodes: [
      n('start', 'Start', 0, 0, 'start', { description: 'Stub -- TODO' }),
      n('setDefault', 'Set Default Output', 0, 0, 'process', { description: 'contextJson = {}' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'setDefault'),
      e('setDefault', 'done'),
    ],
  },
  // TriagePanelReviewWorkflow.cs — Stub (SetOutput + Finish sequence)
  'triage-panel-review': {
    nodes: [
      n('start', 'Start', 0, 0, 'start', { description: 'Stub -- TODO' }),
      n('setDefault', 'Set Default Output', 0, 0, 'process', { description: 'panelResultJson = {}' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'setDefault'),
      e('setDefault', 'done'),
    ],
  },
  // TriagePODecisionWorkflow.cs — Stub (SetOutput + Finish sequence)
  'triage-po-decision': {
    nodes: [
      n('start', 'Start', 0, 0, 'start', { description: 'Stub -- TODO' }),
      n('setDefault', 'Set Default Output', 0, 0, 'process', { description: 'decisionJson = {}' }),
      n('done', 'Finish', 0, 0, 'end'),
    ],
    edges: [
      e('start', 'setDefault'),
      e('setDefault', 'done'),
    ],
  },
  'architecture-flow': {
    nodes: [
      n('gh', 'GitHub Webhook', 0, 0, 'start'),
      n('cli', 'CLI Command', 0, 0, 'start'),
      n('api', 'REST API', 0, 0, 'start'),
      n('adl', 'ADL Orchestrator', 0, 0, 'subworkflow'),
      n('sic', 'Single Issue Cycle', 0, 0, 'subworkflow'),
      n('pg', 'Plan Generation', 0, 0),
      n('tdd', 'TDD Cycle', 0, 0, 'subworkflow'),
      n('cr', 'Code Review', 0, 0, 'subworkflow'),
      n('merge', 'Merge', 0, 0),
      n('chain', 'Provider Chain', 0, 0, 'parallel', { items: ['Claude', 'OpenAI', 'OpenRouter', 'Local'] }),
      n('cb', 'Circuit Breaker', 0, 0, 'decision'),
      n('resolver', 'Role-Based Resolver', 0, 0),
      n('github', 'GitHub API', 0, 0),
      n('gitlab', 'GitLab API', 0, 0),
      n('pg_db', 'PostgreSQL', 0, 0),
      n('rmq', 'RabbitMQ', 0, 0),
      n('chroma', 'ChromaDB', 0, 0),
    ],
    edges: [
      e('gh', 'adl'), e('cli', 'adl'), e('api', 'adl'),
      e('adl', 'sic'), e('sic', 'pg'), e('pg', 'tdd'),
      e('tdd', 'cr'), e('cr', 'merge'),
      e('pg', 'chain'), e('tdd', 'chain'),
      e('chain', 'cb'), e('cb', 'resolver'),
      e('merge', 'github'), e('merge', 'gitlab'),
      e('adl', 'pg_db'), e('adl', 'rmq'),
      e('tdd', 'chroma'),
    ],
  },
  'security-pipeline': {
    nodes: [
      n('input', 'User/LLM Input', 0, 0, 'start'),
      n('sanitize', 'Content Sanitizer', 0, 0, 'process', { description: 'HTML strip, zero-width removal' }),
      n('harden', 'Prompt Hardening', 0, 0, 'process', { description: 'Anti-extraction preamble' }),
      n('llm', 'LLM Call', 0, 0, 'subworkflow'),
      n('validate', 'Tool Validator', 0, 0, 'process', { description: 'Allowlist + schema check' }),
      n('gate', 'Action Gate', 0, 0, 'decision'),
      n('exec', 'Tool Executor', 0, 0),
      n('redact', 'RedactSecrets', 0, 0, 'process', { description: '10 secret patterns' }),
      n('output', 'Output Validator', 0, 0),
      n('clean', 'Clean Output', 0, 0, 'end'),
      n('block', 'Blocked', 0, 0, 'end'),
    ],
    edges: [
      e('input', 'sanitize'), e('sanitize', 'harden'), e('harden', 'llm'),
      e('llm', 'validate'), e('validate', 'gate'),
      e('gate', 'exec', 'Allowed'), e('gate', 'block', 'Denied', 'right'),
      e('exec', 'redact'), e('redact', 'output'), e('output', 'clean'),
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

// --- Auto-layout with ELK (minimizes edge crossings) ---

const elk = new ELK();

async function autoLayoutAsync(nodes: Node[], edges: Edge[]): Promise<{ nodes: Node[]; edges: Edge[] }> {
  const elkNodes = nodes.map((node) => {
    const width = node.type === 'parallel' ? 240 : node.type === 'decision' ? 160 : 180;
    const height = node.type === 'parallel' ? 110 : node.type === 'decision' ? 90 : 65;
    return { id: node.id, width, height };
  });

  const elkEdges = edges.map((edge, i) => ({
    id: edge.id || `e${i}`,
    sources: [edge.source],
    targets: [edge.target],
  }));

  const graph = await elk.layout({
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'DOWN',
      'elk.spacing.nodeNode': '80',
      'elk.layered.spacing.nodeNodeBetweenLayers': '100',
      'elk.layered.crossingMinimization.strategy': 'LAYER_SWEEP',
      'elk.layered.nodePlacement.strategy': 'NETWORK_SIMPLEX',
      'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
      'elk.edgeRouting': 'ORTHOGONAL',
      'elk.layered.mergeEdges': 'true',
    },
    children: elkNodes,
    edges: elkEdges,
  });

  const layoutedNodes = nodes.map((node) => {
    const elkNode = graph.children?.find((n) => n.id === node.id);
    return {
      ...node,
      position: {
        x: elkNode?.x ?? 0,
        y: elkNode?.y ?? 0,
      },
    };
  });

  return { nodes: layoutedNodes, edges };
}

// Synchronous wrapper using cached layout
function autoLayout(nodes: Node[], edges: Edge[]): { nodes: Node[]; edges: Edge[] } {
  // Fallback: simple grid layout until async ELK completes
  const cols = Math.ceil(Math.sqrt(nodes.length));
  return {
    nodes: nodes.map((node, i) => ({
      ...node,
      position: {
        x: (i % cols) * 250,
        y: Math.floor(i / cols) * 120,
      },
    })),
    edges,
  };
}

export default function WorkflowDiagram({ slug, flowSteps }: Props) {
  const rawDiagram = useMemo(() => {
    if (WORKFLOW_DIAGRAMS[slug]) return WORKFLOW_DIAGRAMS[slug];
    if (flowSteps && flowSteps.length > 2) return generateFallback(flowSteps);
    return null;
  }, [slug, flowSteps]);

  // Start with grid fallback, then async ELK layout replaces it
  const fallback = useMemo(() => {
    if (!rawDiagram) return null;
    return autoLayout(rawDiagram.nodes, rawDiagram.edges);
  }, [rawDiagram]);

  const [nodes, setNodes, onNodesChange] = useNodesState(fallback?.nodes ?? []);
  const [edges, setEdges, onEdgesChange] = useEdgesState(fallback?.edges ?? []);
  const [layoutDone, setLayoutDone] = useState(false);

  // Run ELK async layout
  useEffect(() => {
    if (!rawDiagram) return;
    setLayoutDone(false);
    autoLayoutAsync(rawDiagram.nodes, rawDiagram.edges).then((result) => {
      setNodes(result.nodes);
      setEdges(result.edges);
      setLayoutDone(true);
    });
  }, [rawDiagram]);

  if (!fallback) return null;

  return (
    <div className="my-6">
      <div
        className="bg-[#0c0c0e] border border-zinc-800 rounded-xl overflow-hidden"
        style={{ height: '600px' }}
      >
        <ReactFlow
          nodes={nodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.3 }}
          minZoom={0.2}
          maxZoom={2}
          panOnDrag={[0]}
          zoomOnScroll
          selectionOnDrag={[2]}
          multiSelectionKeyCode="Shift"
          selectionMode={1}
          selectionKeyCode="Shift"
          nodesDraggable={true}
          nodesConnectable={false}
          proOptions={{ hideAttribution: true }}
          defaultEdgeOptions={{ type: 'default', ...edgeDefaults }}
        >
          <Background color="#27272a" gap={20} variant={BackgroundVariant.Dots} />
          <Controls className="!bg-zinc-800 !border-zinc-700 !rounded-lg [&_button]:!bg-zinc-800 [&_button]:!border-zinc-700 [&_button]:!text-zinc-400 [&_button:hover]:!bg-zinc-700" />
          <MiniMap
            className="!bg-zinc-900 !border-zinc-800 !rounded-lg"
            nodeColor={(n) => {
              if (n.type === 'start') return '#22c55e';
              if (n.type === 'end') return '#ef4444';
              if (n.type === 'decision') return '#f59e0b';
              if (n.type === 'subworkflow') return '#3b82f6';
              if (n.type === 'parallel') return '#a855f7';
              return '#52525b';
            }}
            maskColor="rgba(0,0,0,0.7)"
          />
        </ReactFlow>
      </div>
    </div>
  );
}

export { WORKFLOW_DIAGRAMS };
export type { FlowStep };
