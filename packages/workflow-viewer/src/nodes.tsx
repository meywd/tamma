import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { kindOf } from './kinds';
import type { WorkflowNode } from './types';

export interface WorkflowNodeData extends Record<string, unknown> {
  node: WorkflowNode;
}

export type WorkflowFlowNode = Node<WorkflowNodeData, 'workflowNode'>;

/**
 * A single kind-coded workflow node. Color and icon come from the node's
 * `kind`; a small badge marks sub-workflow links and api-call nodes so the
 * graph reads at a glance.
 */
export function WorkflowNodeView({ data, selected }: NodeProps<WorkflowFlowNode>) {
  const node = data.node;
  const k = kindOf(node.kind);
  const isSub = node.kind === 'dispatch-subworkflow' || Boolean(node.subWorkflowId);
  const isApi = node.kind === 'api-call' || Boolean(node.api);

  return (
    <div
      className="twv-node"
      data-kind={node.kind}
      data-selected={selected ? 'true' : 'false'}
      style={{ '--twv-accent': k.color, '--twv-bg': k.bg } as React.CSSProperties}
      title={node.name}
    >
      <Handle type="target" position={Position.Top} className="twv-handle" />
      <div className="twv-node-head">
        <svg className="twv-node-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.6} aria-hidden="true">
          <path strokeLinecap="round" strokeLinejoin="round" d={k.icon} />
        </svg>
        <span className="twv-node-label">{node.name}</span>
      </div>
      <div className="twv-node-tags">
        <span className="twv-tag" data-kind={node.kind}>{k.label}</span>
        {isSub && node.subWorkflowResolves && <span className="twv-tag twv-tag-link">↳ open</span>}
        {isApi && <span className="twv-tag twv-tag-api">API</span>}
      </div>
      <Handle type="source" position={Position.Bottom} className="twv-handle" />
    </div>
  );
}
